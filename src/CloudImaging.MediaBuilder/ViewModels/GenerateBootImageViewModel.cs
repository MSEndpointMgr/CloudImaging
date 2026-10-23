using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CloudImaging.Contracts.Models;
using CloudImaging.MediaBuilder.Services;
using Microsoft.Win32;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>
/// View model for the GenerateBootImageView (T153, FR-051a, FR-051b).
/// </summary>
public sealed class GenerateBootImageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly BootImageGenerationService _genService;
    private readonly EntraAuthenticationService _authService;
    private readonly GitHubReleasesClient _gitHubReleasesClient;
    private readonly Action _navigateBack;
    private readonly StringBuilder _logBuilder = new();
    private readonly DispatcherTimer _elapsedTimer;
    private DateTime _generationStartedAtUtc;
    private int _heartbeatLineStart = -1;
    private CancellationTokenSource? _cts;
    /// <summary>
    /// Which stage of the generation workflow is currently executing, so a failure can be
    /// attributed to the right support-reference stage code (T160/FR-058): DWN (client
    /// binaries download), APL (image mount/apply/generation).
    /// </summary>
    private string _currentStage = "DWN";

    /// <summary>Label of the one step that is skipped (not done) when no driver path is given.</summary>
    private const string DriverInjectionStepLabel = "Inject drivers (optional)";

    /// <summary>Label of the one step that is skipped (not done) when no tools path is given.</summary>
    private const string ToolsInjectionStepLabel = "Copy support tools (optional)";

    /// <summary>Label of the step that only runs for a Windows RE base.</summary>
    private const string WindowsReStepLabel = "Extract Windows RE";

    private bool _useGitHubSource = true;
    private string _localSourcePath = string.Empty;
    private string _outputFolderPath = GetDefaultOutputFolder();
    private string _driverRootPath = string.Empty;
    private string _toolsRootPath = string.Empty;
    private bool _enableCommandPromptAccess;
    private bool _includeAutopilotTooling = true;
    private string? _autopilotToolingUnavailableReason;
    private readonly Func<MachineArchitecture, string?> _autopilotToolingCheck;
    private bool _isArm64Selected;
    private bool _useWindowsRe;
    private string _windowsSourcePath = string.Empty;
    private bool _isCheckingSource;
    private SourceCheckResult? _sourceCheck;
    private int _sourceCheckVersion;
    private readonly Func<string, MachineArchitecture, CancellationToken, Task<SourceCheckResult>> _checkSource;
    private bool _isGenerating;
    private bool _isCancelling;
    private bool _isComplete;
    private bool _wasCancelled;
    private int _progressPercent;
    private string _progressMessage = string.Empty;
    private string _elapsedTimeText = string.Empty;
    private string? _resultFilePath;
    private string? _errorMessage;
    private string? _errorReference;
    private string _statusMessage = string.Empty;
    private bool _isResultDialogOpen;

    /// <summary>Builds the view model over the generation service, Entra auth, and GitHub releases client.</summary>
    public GenerateBootImageViewModel(
        BootImageGenerationService genService,
        EntraAuthenticationService authService,
        GitHubReleasesClient gitHubReleasesClient,
        Action navigateBack,
        Func<MachineArchitecture, string?>? autopilotToolingCheck = null,
        Func<string, MachineArchitecture, CancellationToken, Task<SourceCheckResult>>? sourceCheck = null)
    {
        _genService           = genService;
        _authService          = authService;
        _gitHubReleasesClient = gitHubReleasesClient;
        _navigateBack         = navigateBack;
        _autopilotToolingCheck = autopilotToolingCheck ?? AutopilotTooling.GetUnavailableReason;
        _checkSource = sourceCheck ?? WindowsRecoverySource.CheckAsync;
        RefreshAutopilotToolingAvailability();

        Steps = new ObservableCollection<GenerationStep>(CreateSteps());

        // Ticks the progress header's "Xm Ys elapsed" caption once a second while generating.
        // A timer (rather than deriving it from log timestamps) keeps it ticking smoothly even
        // through quiet steps that emit no log/heartbeat lines of their own for a while.
        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedTimeText();

        _genService.ProgressChanged += (_, e) => OnUi(() =>
        {
            ProgressMessage = e.Message;
            ProgressPercent = e.Percent;
            UpdateSteps(e.Percent);
            OnPropertyChanged(nameof(StepProgressLabel));

            // Announce the step transition in the log itself as a banner line. Without this,
            // the log only ever shows raw external-process output (dism.exe/cmd.exe/copype.cmd
            // stdout): a step could go Active in the steps sidebar with nothing in the log to mark
            // the transition, and the next thing shown might be an unrelated-sounding line from that
            // step's own internal tooling (e.g. copype.cmd's own "Mounting ..." chatter), making the
            // log look out of sync with the sidebar. This is done here (once, at the ViewModel) rather
            // than in the service's ReportProgress, since that method also drives the IPC round-trip
            // for elevated generation (see BootImageGenerationService.TailProgress): logging there
            // would double up every banner line during elevated runs. Step messages are kept short
            // and self-contained (no trailing "…") so they always fit the one-line progress header
            // on their own: the same text is reused verbatim as the banner line here.
            AppendLogLine(e.Message.TrimEnd());
        });

        _genService.LogMessage += (_, line) => OnUi(() => AppendLogLine(line));
        _genService.LogHeartbeat += (_, line) => OnUi(() => UpdateHeartbeatLine(line));

        GenerateCommand    = new RelayCommand(async _ => await GenerateAsync(), _ => CanGenerate);
        CancelCommand      = new RelayCommand(_ => Cancel(), _ => IsGenerating && !IsCancelling);
        BrowseCommand      = new RelayCommand(_ => BrowseLocalPath());
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutputFolder());
        BrowseDriverRootCommand = new RelayCommand(_ => BrowseDriverRoot());
        BrowseToolsRootCommand = new RelayCommand(_ => BrowseToolsRoot());
        BrowseWindowsSourceCommand = new RelayCommand(_ => BrowseWindowsSource(), _ => !IsGenerating && !IsCheckingSource);
        BackCommand        = new RelayCommand(_ => _navigateBack(), _ => !IsGenerating);
        NewGenerationCommand = new RelayCommand(_ => ResetToConfiguration(), _ => !IsGenerating);
        OpenResultFolderCommand = new RelayCommand(_ => OpenOutputFolder(), _ => HasResultFilePath);
        CopyResultPathCommand = new RelayCommand(_ => OperationResultText.TryCopyToClipboard(ResultFilePath), _ => HasResultFilePath);
        CopyErrorDetailsCommand = new RelayCommand(_ => OperationResultText.TryCopyToClipboard(ErrorDetails), _ => HasError);
        DismissResultCommand = new RelayCommand(_ => IsResultDialogOpen = false);
    }

    /// <summary>True when the client binaries are downloaded from the latest GitHub release (mutually exclusive with <see cref="UseLocalSource"/>).</summary>
    public bool UseGitHubSource
    {
        get => _useGitHubSource;
        set { _useGitHubSource = value; UseLocalSource = !value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    /// <summary>True when the client binaries are taken from <see cref="LocalSourcePath"/> instead of GitHub.</summary>
    public bool UseLocalSource
    {
        get => !_useGitHubSource;
        set { _useGitHubSource = !value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    /// <summary>Local folder to source pre-built client binaries from when <see cref="UseLocalSource"/> is true.</summary>
    public string LocalSourcePath
    {
        get => _localSourcePath;
        set { _localSourcePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    /// <summary>Folder the generated boot image WIM is written to.</summary>
    public string OutputFolderPath
    {
        get => _outputFolderPath;
        set { _outputFolderPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    /// <summary>
    /// Optional root folder of pre-staged storage/network drivers to inject into the
    /// boot image WIM (FR-051c). Empty means no driver injection.
    /// </summary>
    public string DriverRootPath
    {
        get => _driverRootPath;
        set { _driverRootPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    /// <summary>
    /// Optional root folder of pre-staged troubleshooting utilities to copy
    /// into the boot image WIM at <c>X:\Tools</c>, for manual use from the command prompt
    /// (FR-051d). Empty means nothing is copied.
    /// </summary>
    public string ToolsRootPath
    {
        get => _toolsRootPath;
        set { _toolsRootPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    /// <summary>
    /// Opt-in (default off, FR-051d). When enabled, stamps <c>SupportTools:CommandPromptEnabled</c>
    /// into the Client's appsettings.json, which shows a "Command Prompt" button on the
    /// Operation Selection screen granting full unrestricted WinPE shell access. A deliberate
    /// technician support/diagnostics capability, not enabled by default since it bypasses the
    /// entire guided imaging workflow.
    /// </summary>
    public bool EnableCommandPromptAccess
    {
        get => _enableCommandPromptAccess;
        set { _enableCommandPromptAccess = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Stage OA3Tool and the TPM provider so the Client can capture a Windows Autopilot hardware
    /// hash. On by default where possible: the Client only offers it when the portal enables it.
    /// </summary>
    public bool IncludeAutopilotTooling
    {
        get => _includeAutopilotTooling && IsAutopilotToolingAvailable;
        set { _includeAutopilotTooling = value; OnPropertyChanged(); }
    }

    /// <summary>True when Autopilot hardware-hash tooling can be included for the currently selected architecture on this machine.</summary>
    public bool IsAutopilotToolingAvailable => _autopilotToolingUnavailableReason is null;

    /// <summary>Why the tooling cannot be included for the selected architecture on this machine.</summary>
    public string? AutopilotToolingUnavailableReason => _autopilotToolingUnavailableReason;

    /// <summary>True when <see cref="AutopilotToolingUnavailableReason"/> has an explanation to display.</summary>
    public bool HasAutopilotToolingUnavailableReason => _autopilotToolingUnavailableReason is not null;

    private void RefreshAutopilotToolingAvailability()
    {
        _autopilotToolingUnavailableReason = _autopilotToolingCheck(SelectedArchitecture);
        OnPropertyChanged(nameof(IsAutopilotToolingAvailable));
        OnPropertyChanged(nameof(AutopilotToolingUnavailableReason));
        OnPropertyChanged(nameof(HasAutopilotToolingUnavailableReason));
        OnPropertyChanged(nameof(IncludeAutopilotTooling));
    }

    /// <summary>
    /// Target processor architecture for the generated boot image (todo/arm64-support.md,
    /// Milestone 1). No implicit default toward either radio button is rendered in the view
    /// (both are explicit) but the backing field defaults to x64 so an untouched view model
    /// still behaves like every boot image generated before this feature existed.
    /// </summary>
    public bool IsX64Selected
    {
        get => !_isArm64Selected;
        set
        {
            _isArm64Selected = !value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsArm64Selected));
            OnPropertyChanged(nameof(SelectedArchitecture));
            RefreshAutopilotToolingAvailability();
            _ = CheckWindowsSourceAsync();
        }
    }

    /// <summary>True when ARM64 is the selected target architecture (mutually exclusive with <see cref="IsX64Selected"/>).</summary>
    public bool IsArm64Selected
    {
        get => _isArm64Selected;
        set
        {
            _isArm64Selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsX64Selected));
            OnPropertyChanged(nameof(SelectedArchitecture));
            RefreshAutopilotToolingAvailability();
            _ = CheckWindowsSourceAsync();
        }
    }

    /// <summary>Build on the ADK's Windows PE: wired network only, smallest image (default).</summary>
    public bool IsWindowsPeSelected
    {
        get => !_useWindowsRe;
        set => SetWindowsRe(!value);
    }

    /// <summary>Build on the Windows RE inside <see cref="WindowsSourcePath"/>, which adds Wi-Fi.</summary>
    public bool IsWindowsReSelected
    {
        get => _useWindowsRe;
        set => SetWindowsRe(value);
    }

    /// <summary>Windows ISO or install.wim the Windows RE base is taken from.</summary>
    public string WindowsSourcePath
    {
        get => _windowsSourcePath;
        set
        {
            _windowsSourcePath = value;
            OnPropertyChanged();
            _ = CheckWindowsSourceAsync();
        }
    }

    /// <summary>True while the Windows source is being checked; Generate stays disabled meanwhile.</summary>
    public bool IsCheckingSource
    {
        get => _isCheckingSource;
        private set { _isCheckingSource = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSourceEditable)); OnPropertyChanged(nameof(CanGenerate)); OnPropertyChanged(nameof(SourceCheckPassed)); OnPropertyChanged(nameof(SourceCheckFailed)); }
    }

    /// <summary>False while the source is being checked, so the base and path cannot change mid-check.</summary>
    public bool IsSourceEditable => !IsCheckingSource;

    /// <summary>True when the source passed every check (green check in the path field).</summary>
    public bool SourceCheckPassed => !IsCheckingSource && _sourceCheck is { IsValid: true };

    /// <summary>True when the source has a blocking problem (red cross in the path field).</summary>
    public bool SourceCheckFailed => !IsCheckingSource && _sourceCheck is { IsValid: false };

    /// <summary>What was found, or every problem with its fix, one per line. Shown when hovering the check.</summary>
    public string SourceCheckDetails => _sourceCheck switch
    {
        null => string.Empty,
        { IsValid: true } ok => ok.Summary,
        var failed => string.Join(Environment.NewLine, failed.Problems.Select(p => $"{p.Problem} {p.Fix}")),
    };

    private void SetWindowsRe(bool value)
    {
        if (_useWindowsRe == value)
            return;
        _useWindowsRe = value;
        OnPropertyChanged(nameof(IsWindowsPeSelected));
        OnPropertyChanged(nameof(IsWindowsReSelected));
        OnPropertyChanged(nameof(CanGenerate));
        _ = CheckWindowsSourceAsync();
    }

    /// <summary>
    /// Re-checks the Windows source against the ADK and the target architecture. A newer check
    /// (path or architecture changed again) supersedes one still running.
    /// </summary>
    private async Task CheckWindowsSourceAsync()
    {
        var version = ++_sourceCheckVersion;
        if (!_useWindowsRe || string.IsNullOrWhiteSpace(_windowsSourcePath))
        {
            SetSourceCheck(null);
            IsCheckingSource = false;
            return;
        }

        IsCheckingSource = true;
        SourceCheckResult result;
        try
        {
            result = await _checkSource(_windowsSourcePath, SelectedArchitecture, CancellationToken.None);
        }
        catch (Exception ex)
        {
            result = new SourceCheckResult(string.Empty, [new SourceProblem($"The source could not be checked: {ex.Message}", "Choose another Windows installation source.")]);
        }

        if (version != _sourceCheckVersion)
            return;
        SetSourceCheck(result);
        IsCheckingSource = false;
    }

    private void SetSourceCheck(SourceCheckResult? result)
    {
        _sourceCheck = result;
        OnPropertyChanged(nameof(SourceCheckPassed));
        OnPropertyChanged(nameof(SourceCheckFailed));
        OnPropertyChanged(nameof(SourceCheckDetails));
        OnPropertyChanged(nameof(CanGenerate));
    }

    /// <summary>The architecture the generated boot image targets, derived from <see cref="IsArm64Selected"/>.</summary>
    public MachineArchitecture SelectedArchitecture => IsArm64Selected ? MachineArchitecture.Arm64 : MachineArchitecture.X64;

    /// <summary>True while a boot image generation is in progress.</summary>
    public bool IsGenerating
    {
        get => _isGenerating;
        private set
        {
            _isGenerating = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanGenerate));
            OnPropertyChanged(nameof(ShowProgressView));
            OnPropertyChanged(nameof(ProgressTitle));
            OnPropertyChanged(nameof(ProgressSubtitle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// True from the moment Cancel is clicked until generation actually stops (FR-051 cancel
    /// support). Disables the Cancel button the instant it's pressed: clicking cancel a second
    /// time (or repeatedly) while cleanup is still in flight does nothing useful and just reads
    /// as unresponsive: and flips the spinner's rotation direction as a clear visual cue that a
    /// cancellation is in progress rather than the generation itself just running as normal.
    /// </summary>
    public bool IsCancelling
    {
        get => _isCancelling;
        private set { _isCancelling = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

    /// <summary>True once generation has finished successfully.</summary>
    public bool IsComplete
    {
        get => _isComplete;
        private set
        {
            _isComplete = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowProgressView));
            OnPropertyChanged(nameof(ProgressTitle));
            OnPropertyChanged(nameof(ProgressSubtitle));
        }
    }

    /// <summary>True once the user has cancelled a running generation (FR-051 cancel support).</summary>
    public bool WasCancelled
    {
        get => _wasCancelled;
        private set
        {
            _wasCancelled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowProgressView));
            OnPropertyChanged(nameof(ProgressTitle));
            OnPropertyChanged(nameof(ProgressSubtitle));
            OnPropertyChanged(nameof(CanRetry));
        }
    }

    /// <summary>Overall generation progress, 0-100.</summary>
    public int ProgressPercent
    {
        get => _progressPercent;
        private set { _progressPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressPercentText)); }
    }

    /// <summary>Numeric percentage as display text (e.g. "42%"): the progress bar itself only shows the fill visually.</summary>
    public string ProgressPercentText => $"{ProgressPercent}%";

    /// <summary>Current status line shown above the progress bar.</summary>
    public string ProgressMessage
    {
        get => _progressMessage;
        private set { _progressMessage = value; OnPropertyChanged(); }
    }

    /// <summary>"Xm Ys elapsed" caption, ticking once a second while generating and freezing once it stops.</summary>
    public string ElapsedTimeText
    {
        get => _elapsedTimeText;
        private set { _elapsedTimeText = value; OnPropertyChanged(); }
    }

    /// <summary>Full path of the generated WIM once generation completes successfully; <c>null</c> otherwise.</summary>
    public string? ResultFilePath
    {
        get => _resultFilePath;
        private set
        {
            _resultFilePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasResultFilePath));
            OnPropertyChanged(nameof(ResultFileName));
            OnPropertyChanged(nameof(ResultFolder));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Whether a finished WIM path is available to display/reveal (i.e. generation completed successfully).</summary>
    public bool HasResultFilePath => ResultFilePath is not null;

    /// <summary>File name part of <see cref="ResultFilePath"/>.</summary>
    public string? ResultFileName => ResultFilePath is null ? null : Path.GetFileName(ResultFilePath);

    /// <summary>Folder part of <see cref="ResultFilePath"/>.</summary>
    public string? ResultFolder => ResultFilePath is null ? null : Path.GetDirectoryName(ResultFilePath);

    /// <summary>Icon for the output file row in the result dialog.</summary>
    public Wpf.Ui.Controls.SymbolRegular ResultFileSymbol { get; } = Wpf.Ui.Controls.SymbolRegular.Archive24;

    /// <summary>User-facing reason when generation fails, without the support reference; <c>null</c> while generation hasn't failed.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            _errorMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ShowProgressView));
            OnPropertyChanged(nameof(ProgressTitle));
            OnPropertyChanged(nameof(ProgressSubtitle));
            OnPropertyChanged(nameof(CanRetry));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Support reference code for the failure, shown on its own line in the result dialog.</summary>
    public string? ErrorReference
    {
        get => _errorReference;
        private set { _errorReference = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasErrorReference)); }
    }

    /// <summary>True when <see cref="ErrorReference"/> has a value.</summary>
    public bool HasErrorReference => ErrorReference is not null;

    /// <summary>Result dialog title on failure.</summary>
    public string ErrorTitle { get; } = "Generation failed";

    /// <summary>Title, message and reference as one block, for pasting into a support ticket.</summary>
    public string ErrorDetails => OperationResultText.ErrorDetails(ErrorTitle, ErrorMessage, ErrorReference);

    /// <summary>Result dialog title on success.</summary>
    public string SuccessTitle { get; } = "Boot image generated";

    /// <summary>Result dialog summary on success: architecture, size and the publish hint.</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    /// <summary>Result dialog title when cancelled.</summary>
    public string CancelledTitle { get; } = "Generation cancelled";

    /// <summary>Result dialog message when cancelled.</summary>
    public string CancelledMessage { get; } = "No boot image was saved. Temporary files were cleaned up.";

    /// <summary>True while the result dialog is open; closing it leaves the log and steps visible.</summary>
    public bool IsResultDialogOpen
    {
        get => _isResultDialogOpen;
        private set { _isResultDialogOpen = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowResultDialog)); }
    }

    /// <summary>True when a finished run's result dialog should be shown.</summary>
    public bool ShowResultDialog => IsResultDialogOpen && (IsComplete || HasError || WasCancelled);

    /// <summary>
    /// True once a generation has ended in failure or cancellation: the only cases worth
    /// offering a "start over" button for. A successful generation has nothing to retry, so
    /// that button is hidden once <see cref="IsComplete"/> is true (see GenerateBootImageView.xaml).
    /// </summary>
    public bool CanRetry => HasError || WasCancelled;

    /// <summary>Static status line shown next to the GitHub source option.</summary>
    public string GitHubStatus { get; } = "Latest release will be resolved automatically";

    /// <summary>
    /// Live command/output log streamed from the generation service (FR-051d), as one plain,
    /// selectable block of text (one line per entry, no wrapping): bind a read-only TextBox
    /// to this rather than an ItemsControl/ListBox so the whole log can be selected and copied
    /// like a real console, not a list of separately-selectable rows.
    /// </summary>
    public string LogText => _logBuilder.ToString();

    /// <summary>Ordered workflow steps shown with live status in the progress view.</summary>
    public ObservableCollection<GenerationStep> Steps { get; }

    /// <summary>"Step X of Y" caption shown next to the progress message (mirrors the mockup's progress header).</summary>
    public string StepProgressLabel
    {
        get
        {
            var activeIndex = -1;
            for (var i = 0; i < Steps.Count; i++)
            {
                if (Steps[i].State == GenerationStepState.Active) { activeIndex = i; break; }
            }

            if (activeIndex >= 0)
                return $"Step {activeIndex + 1} of {Steps.Count}";

            var doneOrSkipped = Steps.Count(s => s.State is GenerationStepState.Done or GenerationStepState.Skipped);
            return $"Step {Math.Min(doneOrSkipped + 1, Steps.Count)} of {Steps.Count}";
        }
    }

    /// <summary>
    /// True once generation has started (or finished / failed / cancelled): the view swaps its
    /// configuration cards for the full progress + steps + log layout.
    /// </summary>
    public bool ShowProgressView => IsGenerating || IsComplete || HasError || WasCancelled;

    /// <summary>True once generation has ended in failure.</summary>
    public bool HasError    => ErrorMessage is not null;

    /// <summary>
    /// Progress-view heading, reflecting the current end state instead of always reading
    /// "Generating Boot Image" once generation has actually finished, failed, or been cancelled.
    /// </summary>
    public string ProgressTitle =>
        IsComplete    ? "Generated Boot Image" :
        HasError      ? "Generation Failed" :
        WasCancelled  ? "Generation Cancelled" :
        "Generating Boot Image";

    /// <summary>
    /// Short progress-view subheading. Outcome details (path, error, reference) live in the
    /// result dialog, so the header stays one line once generation has finished.
    /// </summary>
    public string ProgressSubtitle =>
        IsComplete    ? "Upload the WIM in the portal under Boot Images to publish it." :
        HasError      ? "The log below shows where generation stopped." :
        WasCancelled  ? "Temporary files were cleaned up." :
        "Mounting the WinPE image and injecting the Cloud Imaging Client. This can take a few minutes.";

    /// <summary>True when the current configuration is valid enough to start generation.</summary>
    public bool CanGenerate => !IsGenerating
        && !string.IsNullOrWhiteSpace(OutputFolderPath)
        && (!UseLocalSource || Directory.Exists(LocalSourcePath))
        && (string.IsNullOrWhiteSpace(DriverRootPath) || Directory.Exists(DriverRootPath))
        && (string.IsNullOrWhiteSpace(ToolsRootPath) || Directory.Exists(ToolsRootPath))
        && (!IsWindowsReSelected || SourceCheckPassed);

    /// <summary>Starts boot image generation.</summary>
    public ICommand GenerateCommand     { get; }
    /// <summary>Cancels an in-progress generation.</summary>
    public ICommand CancelCommand       { get; }
    /// <summary>Browses for the local client binaries source folder.</summary>
    public ICommand BrowseCommand       { get; }
    /// <summary>Browses for the output folder.</summary>
    public ICommand BrowseOutputCommand { get; }
    /// <summary>Browses for the driver injection root folder.</summary>
    public ICommand BrowseDriverRootCommand { get; }
    /// <summary>Browses for the support tools root folder.</summary>
    public ICommand BrowseToolsRootCommand { get; }
    /// <summary>Browses for the Windows ISO or install.wim a Windows RE base is built from.</summary>
    public ICommand BrowseWindowsSourceCommand { get; }
    /// <summary>Navigates back without starting generation.</summary>
    public ICommand BackCommand         { get; }
    /// <summary>Resets the view model back to the configuration screen for another generation.</summary>
    public ICommand NewGenerationCommand { get; }
    /// <summary>Opens File Explorer with the generated WIM pre-selected.</summary>
    public ICommand OpenResultFolderCommand { get; }
    /// <summary>Copies the generated WIM's full path to the clipboard.</summary>
    public ICommand CopyResultPathCommand { get; }
    /// <summary>Copies the failure title, message and reference to the clipboard.</summary>
    public ICommand CopyErrorDetailsCommand { get; }
    /// <summary>Closes the result dialog, leaving the log and steps visible.</summary>
    public ICommand DismissResultCommand { get; }

    /// <summary>
    /// Opens File Explorer with the generated WIM pre-selected. Best-effort: the path stays
    /// visible and copyable in the result dialog regardless.
    /// </summary>
    private void OpenOutputFolder()
    {
        if (ResultFilePath is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{ResultFilePath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0.0} MB";

    private async Task GenerateAsync()
    {
        ClearLog();
        foreach (var step in Steps) step.State = GenerationStepState.Pending;
        OnPropertyChanged(nameof(StepProgressLabel));
        ProgressPercent = 0;
        ProgressMessage = "Starting";
        ResultFilePath  = null;
        IsResultDialogOpen = false;
        IsGenerating  = true;
        IsCancelling  = false;
        IsComplete    = false;
        WasCancelled  = false;
        ErrorReference = null;
        ErrorMessage  = null;

        _generationStartedAtUtc = DateTime.UtcNow;
        UpdateElapsedTimeText();
        _elapsedTimer.Start();

        _cts = new CancellationTokenSource();
        try
        {
            // Set (or refresh) the Operator API bearer token used for boot media certificate
            // retrieval, branding logo retrieval, and Device Gateway endpoint resolution:
            // without this, GenerateElevatedAsync's certificate fetch fails as unauthenticated.
            var token = await _authService.GetAccessTokenAsync(_cts.Token)
                ?? throw new InvalidOperationException("Sign in to the Operator API before generating a boot image.");
            _genService.SetOperatorApiAccessToken(token);

            _currentStage = "DWN";
            string clientBinariesPath = UseGitHubSource
                ? await DownloadLatestClientAsync()
                : LocalSourcePath;

            // GenerateElevatedAsync resolves the boot media certificate, branding logo, and
            // Device Gateway URL internally from the Operator API before transparently
            // relaunching this app elevated (one UAC prompt) if it isn't already running as
            // Administrator, since DISM image mounting requires it.
            _currentStage = "APL";
            var result = await _genService.GenerateElevatedAsync(
                clientBinariesPath,
                pfxBytes: null,
                _outputFolderPath,
                driverRootPath: string.IsNullOrWhiteSpace(_driverRootPath) ? null : _driverRootPath,
                toolsRootPath: string.IsNullOrWhiteSpace(_toolsRootPath) ? null : _toolsRootPath,
                enableCommandPromptAccess: _enableCommandPromptAccess,
                architecture: SelectedArchitecture,
                includeAutopilotTooling: IncludeAutopilotTooling,
                windowsSourcePath: IsWindowsReSelected ? WindowsSourcePath : null,
                ct: _cts.Token);

            ResultFilePath = result.WimPath;
            StatusMessage  = string.Create(CultureInfo.CurrentCulture,
                $"{(SelectedArchitecture == MachineArchitecture.Arm64 ? "ARM64" : "x64")}{(IsWindowsReSelected ? " \u00B7 Windows RE (Wi-Fi)" : string.Empty)} \u00B7 {FormatSize(new FileInfo(result.WimPath).Length)}. Upload it in the portal under Boot Images to publish it.");
            IsComplete     = true;
        }
        catch (OperationCanceledException)
        {
            WasCancelled    = true;
            ProgressMessage = "Generation cancelled.";
            AppendLogLine("Generation cancelled. Cleaning up (unmounting/discarding any in-progress WIM mount, deleting temp files).");
            MarkActiveStepFailed();
        }
        catch (Exception ex)
        {
            // Mirror the "FAILED: ..." convention already used for individual command failures
            // (RunExternalAsync) so the log always ends with a clear terminal marker line,
            // even when the failure happened somewhere that never itself called RaiseLog
            // (e.g. an ADK/prerequisite check): otherwise the log would just trail off with
            // whatever step banner was last announced, with nothing to explain the red InfoBar
            // shown alongside it.
            // T160/FR-058: every failure path surfaces a support reference code so a technician
            // can quote it to support without needing log access.
            var (reason, reference) = OperationResultText.SplitSupportReference(
                OperatorApiErrorDescription.Describe(ex),
                () => SupportReferenceCode.ForMediaBuilder("GENBOOT", _currentStage).ToString());
            ErrorReference = reference;
            ErrorMessage   = reason;
            AppendLogLine($"FAILED: {reason} (Error reference: {reference})");
            MarkActiveStepFailed();
        }
        finally
        {
            IsGenerating = false;
            IsCancelling = false;
            IsResultDialogOpen = true;
            _elapsedTimer.Stop();
            UpdateElapsedTimeText(); // one final tick so the frozen caption reflects the exact stop time
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Recomputes <see cref="ElapsedTimeText"/> from <see cref="_generationStartedAtUtc"/>.</summary>
    private void UpdateElapsedTimeText()
    {
        var elapsed = DateTime.UtcNow - _generationStartedAtUtc;
        ElapsedTimeText = elapsed.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m {elapsed.Seconds}s elapsed")
            : elapsed.TotalMinutes >= 1
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s elapsed")
                : string.Create(CultureInfo.InvariantCulture, $"{elapsed.Seconds}s elapsed");
    }

    /// <summary>
    /// Requests cancellation of a running generation (FR-051 cancel support). The elevated
    /// worker runs the same mount-rollback/temp-cleanup logic it would on any other failure
    /// (see BootImageGenerationService.RunElevatedWorkerAsync) before this call resolves.
    /// </summary>
    private void Cancel()
    {
        if (_cts is null || IsCancelling)
            return;

        // Set IsCancelling (disables the button, reverses the spinner) and log the request
        // immediately, at the moment the user clicks Cancel: the later "Generation cancelled"
        // banner only appears once cleanup has actually finished unwinding, which can take a
        // few seconds (e.g. a DISM unmount already in flight), so without this the button would
        // look unresponsive and the log would show nothing happened until cleanup completes.
        IsCancelling    = true;
        ProgressMessage = "Cancelling. Waiting for cleanup to finish";
        AppendLogLine("Cancellation requested by user. Waiting for cleanup to finish.");
        _cts.Cancel();
    }

    private async Task<string> DownloadLatestClientAsync()
    {
        void OnProgress(object? _, (string Message, int Percent, bool Replace) e) =>
            OnUi(() =>
            {
                ProgressMessage = e.Message;
                // Per-chunk byte progress (Replace) overwrites the same line in place, same as the
                // DISM heartbeat ticker below, instead of flooding the log with one line per tick.
                if (e.Replace)
                    UpdateHeartbeatLine(e.Message);
                else
                    AppendLogLine(e.Message);
            });

        _gitHubReleasesClient.ProgressChanged += OnProgress;
        try
        {
            return await _gitHubReleasesClient.DownloadLatestClientAsync(SelectedArchitecture, _cts!.Token);
        }
        finally
        {
            _gitHubReleasesClient.ProgressChanged -= OnProgress;
        }
    }

    private void BrowseLocalPath()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select Client binaries folder" };
        if (dialog.ShowDialog() == true)
            LocalSourcePath = dialog.FolderName;
    }

    private void BrowseOutputFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select output folder" };
        if (dialog.ShowDialog() == true)
            OutputFolderPath = dialog.FolderName;
    }

    private void BrowseDriverRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select driver root folder (optional)" };
        if (dialog.ShowDialog() == true)
            DriverRootPath = dialog.FolderName;
    }

    private void BrowseWindowsSource()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a Windows ISO or install.wim",
            Filter = "Windows installation source (*.iso;*.wim)|*.iso;*.wim",
        };
        if (dialog.ShowDialog() == true)
            WindowsSourcePath = dialog.FileName;
    }

    private void BrowseToolsRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select support tools folder (optional)" };
        if (dialog.ShowDialog() == true)
            ToolsRootPath = dialog.FolderName;
    }

    /// <summary>Returns to the configuration view to start a fresh generation.</summary>
    private void ResetToConfiguration()
    {
        IsResultDialogOpen = false;
        IsComplete   = false;
        WasCancelled = false;
        ErrorReference = null;
        ErrorMessage = null;
        ResultFilePath = null;
        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        ClearLog();
        foreach (var step in Steps) step.State = GenerationStepState.Pending;
        OnPropertyChanged(nameof(StepProgressLabel));
    }

    /// <summary>
    /// Appends one clean line to <see cref="LogText"/>. External tools (copype.cmd, dism.exe)
    /// emit blank lines and decorative "===...===" banner lines mixed in with the actual
    /// step/status text: those are noise in a live activity log, so they're filtered out here
    /// rather than in the service (which still logs everything, unfiltered, via ILogger).
    /// </summary>
    private void AppendLogLine(string line)
    {
        var trimmed = line.TrimEnd();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.All(c => c is '=' or '-'))
            return;

        // A real line follows any pending heartbeat tick: the next heartbeat should start
        // a fresh line rather than overwrite this one.
        _heartbeatLineStart = -1;
        _logBuilder.Append(TimestampPrefix()).Append(trimmed).Append(Environment.NewLine);
        OnPropertyChanged(nameof(LogText));
    }

    /// <summary>
    /// Updates the trailing progress line in place instead of appending a new one each tick
    /// (DISM's progress bar and the Client download byte count), so a rapidly-ticking status
    /// doesn't flood the log with one line per update.
    /// </summary>
    private void UpdateHeartbeatLine(string line)
    {
        if (_heartbeatLineStart >= 0)
            _logBuilder.Length = _heartbeatLineStart;
        else
            _heartbeatLineStart = _logBuilder.Length;

        _logBuilder.Append(TimestampPrefix()).Append(line.TrimEnd()).Append(Environment.NewLine);
        OnPropertyChanged(nameof(LogText));
    }

    /// <summary>Formats the current local wall-clock time (HH:mm:ss) as a log-line prefix.</summary>
    private static string TimestampPrefix() =>
        string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH\\:mm\\:ss} ");

    private void ClearLog()
    {
        _logBuilder.Clear();
        _heartbeatLineStart = -1;
        OnPropertyChanged(nameof(LogText));
    }

    /// <summary>Marshals an action onto the UI thread (progress/log events may arrive on a worker thread).</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private static IEnumerable<GenerationStep> CreateSteps() =>
    [
        new("Verify ADK installation", 5),
        new("Copy WinPE base files", 15),
        new(WindowsReStepLabel, 22, "Skipped (Windows PE base)"),
        new("Mount WIM for customization", 30),
        new("Inject Cloud Imaging Client", 50),
        new(DriverInjectionStepLabel, 61, "Skipped (no drivers to inject)"),
        new(ToolsInjectionStepLabel, 63, "Skipped (no support tools to copy)"),
        new("Unmount and commit WIM", 75),
        new("Copy output and hash", 88),
        new("Complete", 100),
    ];

    /// <summary>
    /// Advances step status from the overall percent: a step is Done once the percent has
    /// reached the following step's start, Active while the percent is within its own range,
    /// and Pending before then.
    /// </summary>
    private void UpdateSteps(int percent)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            var nextStart = i + 1 < Steps.Count ? Steps[i + 1].StartPercent : 101;
            var isCompleted = (percent >= 100 && i == Steps.Count - 1) || percent >= nextStart;

            if (isCompleted)
            {
                // The driver-injection and support-tools steps are optional (FR-051c/FR-051d):
                // when no path was given for one, nothing was actually done for it, so it reads
                // as Skipped rather than Done.
                var noDriversToInject = step.Label == DriverInjectionStepLabel && string.IsNullOrWhiteSpace(_driverRootPath);
                var noToolsToCopy = step.Label == ToolsInjectionStepLabel && string.IsNullOrWhiteSpace(_toolsRootPath);
                var windowsPeBase = step.Label == WindowsReStepLabel && !_useWindowsRe;
                step.State = (noDriversToInject || noToolsToCopy || windowsPeBase) ? GenerationStepState.Skipped : GenerationStepState.Done;
            }
            else if (percent >= step.StartPercent)
                step.State = GenerationStepState.Active;
            else
                step.State = GenerationStepState.Pending;
        }
    }

    /// <summary>
    /// Called when generation ends in error or cancellation: the step that was still
    /// <see cref="GenerationStepState.Active"/> never reached <see cref="GenerationStepState.Done"/>,
    /// so left alone it would keep rendering as "in progress" forever alongside the terminal
    /// failed/cancelled state shown above it. Marks it (there's at most one) Failed instead.
    /// </summary>
    private void MarkActiveStepFailed()
    {
        var activeStep = Steps.FirstOrDefault(s => s.State == GenerationStepState.Active);
        if (activeStep is not null)
            activeStep.State = GenerationStepState.Failed;
    }

    /// <summary>
    /// Default output location: a "Cloud Imaging Media" subfolder under the current user's
    /// Documents folder. This is writable without elevation. Falls back to the user profile
    /// (then the temp folder) if Documents cannot be resolved.
    /// </summary>
    private static string GetDefaultOutputFolder()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(basePath))
            basePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(basePath))
            basePath = Path.GetTempPath();

        return Path.Combine(basePath, "Cloud Imaging Media Builder", "Boot Images");
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Disposes the in-flight cancellation token source, if any (owned by this view model), and stops the elapsed-time ticker.</summary>
    public void Dispose()
    {
        _elapsedTimer.Stop();
        _cts?.Dispose();
    }
}

/// <summary>Lifecycle state of a single <see cref="GenerationStep"/>.</summary>
public enum GenerationStepState
{
    /// <summary>Not started yet.</summary>
    Pending,

    /// <summary>Currently executing.</summary>
    Active,

    /// <summary>Finished successfully.</summary>
    Done,

    /// <summary>Step was bypassed because its optional input was not provided (e.g. no drivers to inject).</summary>
    Skipped,

    /// <summary>Generation ended (error or cancellation) while this was the active step.</summary>
    Failed,
}

/// <summary>
/// One step of the boot image generation workflow, shown with live status in the progress
/// view. <see cref="StartPercent"/> is the overall-progress percentage at which the step
/// becomes active (see GenerateBootImageViewModel.UpdateSteps).
/// </summary>
public sealed class GenerationStep(string label, int startPercent, string? skippedCaption = null) : INotifyPropertyChanged
{
    private GenerationStepState _state = GenerationStepState.Pending;

    /// <summary>Display label for the step.</summary>
    public string Label { get; } = label;
    /// <summary>Overall-progress percentage at which this step becomes active.</summary>
    public int StartPercent { get; } = startPercent;

    /// <summary>Current lifecycle state of the step.</summary>
    public GenerationStepState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Glyph));
            OnPropertyChanged(nameof(IsSkipped));
            OnPropertyChanged(nameof(Caption));
        }
    }

    /// <summary>True while this step is in the <see cref="GenerationStepState.Skipped"/> state.</summary>
    public bool IsSkipped => _state == GenerationStepState.Skipped;

    /// <summary>Short explanatory line shown under a skipped step; null for every other state.</summary>
    public string? Caption => IsSkipped ? skippedCaption ?? "Skipped" : null;

    /// <summary>Symbol shown next to the step label: done ✓, active ▶, failed ✕, skipped , , pending ○.</summary>
    public string Glyph => _state switch
    {
        GenerationStepState.Done    => "\u2713", // ✓
        GenerationStepState.Active  => "\u25B6", // ▶
        GenerationStepState.Failed  => "\u2715", // ✕
        GenerationStepState.Skipped => "\u2013", // en dash
        _                           => "\u25CB", // ○
    };

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
