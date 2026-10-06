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

public enum AutopilotAvailabilityState
{
    Checking,
    Enabled,
    Disabled,
    Unreachable,
}

public enum AutopilotFlowStage
{
    Capturing,
    Submitting,
    Submitted,
    Waiting,
    Finished,
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
    private string? _locationName;
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

    public ICommand BackCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand WaitForDecisionCommand { get; }

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

    public bool IsBusy => Stage is AutopilotFlowStage.Capturing or AutopilotFlowStage.Submitting;
    public bool IsSubmitted => Stage == AutopilotFlowStage.Submitted;
    public bool IsWaiting => Stage == AutopilotFlowStage.Waiting;
    public bool IsFailed => Stage == AutopilotFlowStage.Failed;
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

    public bool IsPositiveOutcome => Stage == AutopilotFlowStage.Finished
        && RequestState is AutopilotRegistrationState.Imported or AutopilotRegistrationState.AlreadyRegistered;

    public bool IsNegativeOutcome => Stage == AutopilotFlowStage.Finished && !IsPositiveOutcome;

    public string? ReferenceCode { get => _referenceCode; private set { _referenceCode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowDetails)); } }
    public string? SerialNumber { get => _serialNumber; private set { _serialNumber = value; OnPropertyChanged(); } }
    public string? DeviceDescription { get => _deviceDescription; private set { _deviceDescription = value; OnPropertyChanged(); } }
    public string? LocationName { get => _locationName; private set { _locationName = value; OnPropertyChanged(); OnPropertyChanged(nameof(LocationDisplay)); } }
    public string LocationDisplay => string.IsNullOrWhiteSpace(_locationName) ? "Not set" : _locationName;

    /// <summary>Extra line under the subtitle: rejection reason, import error or last poll time.</summary>
    public string? Detail { get => _detail; private set { _detail = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasDetail)); } }
    public bool HasDetail => !string.IsNullOrWhiteSpace(_detail);

    public string? ErrorMessage { get => _errorMessage; private set { _errorMessage = value; OnPropertyChanged(); } }

    /// <summary>Set when the captured hash lacks the TPM 2.0 data pre-provisioning and self-deploying need.</summary>
    public string? TpmWarning { get => _tpmWarning; private set { _tpmWarning = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTpmWarning)); } }
    public bool HasTpmWarning => !string.IsNullOrWhiteSpace(_tpmWarning);

    public string Title => Stage switch
    {
        AutopilotFlowStage.Capturing => "Reading hardware hash",
        AutopilotFlowStage.Submitting => "Submitting for approval",
        AutopilotFlowStage.Submitted => "Submitted for approval",
        AutopilotFlowStage.Waiting => RequestState == AutopilotRegistrationState.Importing ? "Approved, importing" : "Waiting for approval",
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

    public string Subtitle => Stage switch
    {
        AutopilotFlowStage.Capturing => "Collecting the device's Windows Autopilot hardware hash. This takes up to a minute.",
        AutopilotFlowStage.Submitting => "Sending the hardware hash to the Cloud Imaging service.",
        AutopilotFlowStage.Submitted => "An approver reviews this request in the Cloud Imaging portal. You can image this device now; registration does not depend on it.",
        AutopilotFlowStage.Waiting => RequestState == AutopilotRegistrationState.Importing
            ? "Intune is processing the import. This usually takes a few minutes."
            : "Checking for a decision every 30 seconds. You can go back at any time.",
        AutopilotFlowStage.Failed => "Nothing was submitted. Check the network connection and try again.",
        _ => RequestState switch
        {
            AutopilotRegistrationState.Imported => "Windows Autopilot will apply its profile the next time this device runs Windows setup.",
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
            LocationName = identity.LocationName;

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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
