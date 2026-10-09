using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CloudImaging.Client.Services;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.ViewModels;

/// <summary>Identity fields read from WMI and the USB preparation manifest.</summary>
internal sealed record DeviceIdentity(string SerialNumber, string Manufacturer, string Model, Guid? LocationId, string? LocationName);

/// <summary>Whether Autopilot registration should be offered to the operator.</summary>
public enum AutopilotAvailabilityState
{
    /// <summary>Still asking the Device Gateway whether registration is enabled.</summary>
    Checking,

    /// <summary>Registration is enabled for this tenant.</summary>
    Enabled,

    /// <summary>An administrator has turned registration off in the portal.</summary>
    Disabled,

    /// <summary>The imaging service could not be reached to check; offered optimistically.</summary>
    Unreachable,
}

/// <summary>Stage of the Autopilot registration flow.</summary>
public enum AutopilotFlowStage
{
    /// <summary>Reading the device's hardware hash.</summary>
    Capturing,

    /// <summary>Sending the hardware hash to the Device Gateway API.</summary>
    Submitting,

    /// <summary>Submitted; the operator can image the device now or wait for a decision.</summary>
    Submitted,

    /// <summary>Polling for the approver's decision.</summary>
    Waiting,

    /// <summary>Reached a terminal state: imported, rejected, expired, or already registered.</summary>
    Finished,

    /// <summary>Capture or submission failed before reaching the service.</summary>
    Failed,
}

/// <summary>
/// Drives "Register with Autopilot": capture the hardware hash, submit it, and optionally wait
/// for the approver's decision. Never creates an imaging session, so Back to start always leaves
/// the device free to image.
/// </summary>
public sealed partial class AutopilotRegistrationViewModel : INotifyPropertyChanged, IDisposable
{
    internal static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(30);

    private readonly DeviceGatewayApiClient _gateway;
    private readonly AutopilotHashCaptureService _capture;
    private readonly SystemClockSynchronizationService _clockSync;
    private readonly Func<DeviceIdentity> _readIdentity;
    private readonly Action _navigateBack;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger _logger;
    private CancellationTokenSource _cts = new();
    private AutopilotFlowStage _stage = AutopilotFlowStage.Capturing;
    private AutopilotRegistrationState? _requestState;
    private string? _referenceCode;
    private string? _serialNumber;
    private string? _deviceDescription;
    private string? _detail;
    private string? _errorMessage;
    private string? _tpmWarning;
    private Guid _requestId;
    private string? _statusToken;
    private DateTimeOffset? _lastCheckedAt;
    private bool _disposed;

    internal AutopilotRegistrationViewModel(
        DeviceGatewayApiClient gateway,
        AutopilotHashCaptureService capture,
        Action navigateBack,
        SystemClockSynchronizationService? clockSync = null,
        Func<DeviceIdentity>? readIdentity = null,
        ILogger<AutopilotRegistrationViewModel>? logger = null,
        TimeSpan? pollInterval = null)
    {
        _gateway = gateway;
        _capture = capture;
        _navigateBack = navigateBack;
        _clockSync = clockSync ?? new SystemClockSynchronizationService();
        _readIdentity = readIdentity ?? OperationSelectionViewModel.ReadDeviceIdentity;
        _logger = logger ?? NullLogger<AutopilotRegistrationViewModel>.Instance;
        _pollInterval = pollInterval ?? StatusPollInterval;

        BackCommand = new RelayCommand(_ => GoBack());
        RetryCommand = new RelayCommand(_ => Start(), _ => Stage == AutopilotFlowStage.Failed);
        WaitForDecisionCommand = new RelayCommand(_ => StartWaiting(), _ => Stage == AutopilotFlowStage.Submitted);
    }

    /// <summary>Cancels the flow and returns to operation selection.</summary>
    public ICommand BackCommand { get; }

    /// <summary>Restarts the flow from capture, enabled only after a failure.</summary>
    public ICommand RetryCommand { get; }

    /// <summary>Starts polling for the approver's decision, enabled only once submitted.</summary>
    public ICommand WaitForDecisionCommand { get; }

    /// <summary>Current stage of the flow.</summary>
    public AutopilotFlowStage Stage
    {
        get => _stage;
        private set
        {
            _stage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsSubmitted));
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsPositiveOutcome));
            OnPropertyChanged(nameof(IsNegativeOutcome));
            OnPropertyChanged(nameof(ShowDetails));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>True while capturing or submitting.</summary>
    public bool IsBusy => Stage is AutopilotFlowStage.Capturing or AutopilotFlowStage.Submitting;

    /// <summary>True once the request was submitted and is awaiting a decision.</summary>
    public bool IsSubmitted => Stage == AutopilotFlowStage.Submitted;

    /// <summary>True while actively polling for a decision.</summary>
    public bool IsWaiting => Stage == AutopilotFlowStage.Waiting;

    /// <summary>True when capture or submission failed.</summary>
    public bool IsFailed => Stage == AutopilotFlowStage.Failed;

    /// <summary>True once device identity has been read, so the detail panel can be shown.</summary>
    public bool ShowDetails => _referenceCode is not null;

    /// <summary>Request state reported by the service, null before submission.</summary>
    public AutopilotRegistrationState? RequestState
    {
        get => _requestState;
        private set
        {
            _requestState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPositiveOutcome));
            OnPropertyChanged(nameof(IsNegativeOutcome));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    /// <summary>True once finished with an outcome that counts as a successful registration.</summary>
    public bool IsPositiveOutcome => Stage == AutopilotFlowStage.Finished
        && RequestState is AutopilotRegistrationState.Imported or AutopilotRegistrationState.AlreadyRegistered;

    /// <summary>True once finished with an outcome that is not <see cref="IsPositiveOutcome"/>.</summary>
    public bool IsNegativeOutcome => Stage == AutopilotFlowStage.Finished && !IsPositiveOutcome;

    /// <summary>Short code shown so a technician can find this request in the portal.</summary>
    public string? ReferenceCode { get => _referenceCode; private set { _referenceCode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowDetails)); } }

    /// <summary>Device serial number.</summary>
    public string? SerialNumber { get => _serialNumber; private set { _serialNumber = value; OnPropertyChanged(); } }

    /// <summary>Manufacturer and model, formatted for display.</summary>
    public string? DeviceDescription { get => _deviceDescription; private set { _deviceDescription = value; OnPropertyChanged(); } }

    /// <summary>Extra line under the subtitle: rejection reason, import error or last poll time.</summary>
    public string? Detail { get => _detail; private set { _detail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDetail)); } }

    /// <summary>True when <see cref="Detail"/> is set.</summary>
    public bool HasDetail => !string.IsNullOrWhiteSpace(_detail);

    /// <summary>Error message shown when <see cref="IsFailed"/>.</summary>
    public string? ErrorMessage { get => _errorMessage; private set { _errorMessage = value; OnPropertyChanged(); } }

    /// <summary>Set when the captured hash lacks the TPM 2.0 data pre-provisioning and self-deploying need.</summary>
    public string? TpmWarning { get => _tpmWarning; private set { _tpmWarning = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTpmWarning)); } }

    /// <summary>True when <see cref="TpmWarning"/> is set.</summary>
    public bool HasTpmWarning => !string.IsNullOrWhiteSpace(_tpmWarning);

    /// <summary>Heading text for the current <see cref="Stage"/>/<see cref="RequestState"/>.</summary>
    public string Title => Stage switch
    {
        AutopilotFlowStage.Capturing => "Reading hardware hash",
        AutopilotFlowStage.Submitting => "Submitting for approval",
        AutopilotFlowStage.Submitted => "Submitted for approval",
        // Approval and the Intune import that follows read as one wait; the outcome screens follow either way.
        AutopilotFlowStage.Waiting => "Waiting for approval",
        AutopilotFlowStage.Failed => "Registration not sent",
        _ => RequestState switch
        {
            AutopilotRegistrationState.Imported => "Registered with Autopilot",
            AutopilotRegistrationState.AlreadyRegistered => "Already registered",
            AutopilotRegistrationState.Rejected => "Request rejected",
            AutopilotRegistrationState.Expired => "Request expired",
            AutopilotRegistrationState.ImportFailed => "Import failed",
            _ => "Registration finished",
        },
    };

    /// <summary>Explanatory text under <see cref="Title"/> for the current <see cref="Stage"/>/<see cref="RequestState"/>.</summary>
    public string Subtitle => Stage switch
    {
        AutopilotFlowStage.Capturing => "Collecting the device's Windows Autopilot hardware hash.\nThis might take some time.",
        AutopilotFlowStage.Submitting => "Sending the hardware hash to the Cloud Imaging service.",
        AutopilotFlowStage.Submitted => "Approval is required. You can image this device in the meantime.",
        AutopilotFlowStage.Waiting => $"Updates automatically every {_pollInterval.TotalSeconds:0} seconds.",
        AutopilotFlowStage.Failed => "Nothing was submitted. Check the network connection and try again.",
        _ => RequestState switch
        {
            AutopilotRegistrationState.Imported => "The device has been imported into Windows Autopilot.",
            AutopilotRegistrationState.AlreadyRegistered => "This device is already a Windows Autopilot device. Nothing else to do.",
            AutopilotRegistrationState.Rejected => "The approver did not import this device.",
            AutopilotRegistrationState.Expired => "Nobody decided in time. Submit the device again to create a new request.",
            AutopilotRegistrationState.ImportFailed => "An approver can retry the import from the portal.",
            _ => string.Empty,
        },
    };

    /// <summary>Captures and submits. Safe to call again after a failure.</summary>
    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        ResetCancellation();
        var ct = _cts.Token;
        ErrorMessage = null;
        Detail = null;
        TpmWarning = null;
        try
        {
            Stage = AutopilotFlowStage.Capturing;
            var identity = await Task.Run(_readIdentity, ct);
            SerialNumber = identity.SerialNumber;
            DeviceDescription = $"{identity.Manufacturer} {identity.Model}".Trim();

            var hash = await _capture.CaptureAsync(ct);

            // An unreadable layout stays silent; only a hash known to lack TPM 2.0 data warns.
            var inspection = AutopilotHardwareHash.Inspect(hash);
            if (inspection.Decoded && !inspection.PreProvisioningReady)
            {
                LogNoTpmData(_logger, inspection.TpmVersion ?? "none", inspection.HasTpmEkPub);
                TpmWarning = "No TPM 2.0 data was captured, so pre-provisioning and self-deploying will not work for this device. Turn on TPM 2.0 in the firmware, then submit again; the new hash replaces this one.";
            }

            // The proof-of-possession timestamp is checked against server time; WinPE clocks are often wrong.
            Stage = AutopilotFlowStage.Submitting;
            await _clockSync.TrySynchronizeAsync(ct);
            var response = await _gateway.SubmitAutopilotHashAsync(new AutopilotHashSubmission
            {
                SerialNumber = identity.SerialNumber,
                Manufacturer = identity.Manufacturer,
                Model = identity.Model,
                HardwareHash = hash,
                Architecture = MachineArchitecturePlatform.HostArchitecture(),
                LocationId = identity.LocationId,
                LocationName = identity.LocationName,
                ClientVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            }, ct);

            _requestId = response.RequestId;
            _statusToken = response.StatusToken;
            ReferenceCode = response.ReferenceCode;
            RequestState = response.State;
            Stage = AutopilotRegistrationRequest.IsTerminal(response.State) ? AutopilotFlowStage.Finished : AutopilotFlowStage.Submitted;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Back to start was pressed.
        }
        catch (Exception ex)
        {
            LogFailed(_logger, ex);
            ErrorMessage = ex.Message;
            Stage = AutopilotFlowStage.Failed;
        }
    }

    private void StartWaiting()
    {
        if (_statusToken is null)
            return;
        Stage = AutopilotFlowStage.Waiting;
        _ = PollAsync(_cts.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await _gateway.GetAutopilotStatusAsync(_requestId, _statusToken!, ct);
                _lastCheckedAt = DateTimeOffset.Now;
                RequestState = status.State;
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));

                if (AutopilotRegistrationRequest.IsTerminal(status.State) || status.State == AutopilotRegistrationState.ImportFailed)
                {
                    Detail = status.State switch
                    {
                        AutopilotRegistrationState.Rejected when !string.IsNullOrWhiteSpace(status.RejectionReason) => $"Reason: {status.RejectionReason}",
                        AutopilotRegistrationState.ImportFailed when !string.IsNullOrWhiteSpace(status.ImportErrorName) => $"Intune reported: {status.ImportErrorName}",
                        AutopilotRegistrationState.Imported when !string.IsNullOrWhiteSpace(status.GroupTag) => $"Group tag: {status.GroupTag}",
                        _ => null,
                    };
                    Stage = AutopilotFlowStage.Finished;
                    OnPropertyChanged(nameof(IsPositiveOutcome));
                    OnPropertyChanged(nameof(IsNegativeOutcome));
                    return;
                }

                Detail = $"Last checked {_lastCheckedAt:HH:mm:ss}.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep waiting through transient outages; the operator can always go back.
                LogPollFailed(_logger, ex);
                Detail = "Could not reach the imaging service. Retrying.";
            }

            try
            {
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void GoBack()
    {
        _cts.Cancel();
        _navigateBack();
    }

    private void ResetCancellation()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
    }

    /// <summary>Cancels any in-flight capture, submission, or polling.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Autopilot registration failed before the request was submitted.")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Autopilot hardware hash has no usable TPM 2.0 data (TPM version {TpmVersion}, endorsement key present {HasEkPub}).")]
    private static partial void LogNoTpmData(ILogger logger, string tpmVersion, bool hasEkPub);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Autopilot status check failed; retrying.")]
    private static partial void LogPollFailed(ILogger logger, Exception ex);

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
