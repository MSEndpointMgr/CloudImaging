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

    private bool _useGitHubSource = true;
    private string _localSourcePath = string.Empty;
    private string _outputFolderPath = GetDefaultOutputFolder();
    private string _driverRootPath = string.Empty;
    private bool _enableCommandPromptAccess;
    private bool _isGenerating;
    private bool _isCancelling;
    private bool _isComplete;
    private bool _wasCancelled;
    private int _progressPercent;
    private string _progressMessage = string.Empty;
    private string _elapsedTimeText = string.Empty;
    private string? _outputWimPath;
    private string? _errorMessage;

    public GenerateBootImageViewModel(
        BootImageGenerationService genService,
        EntraAuthenticationService authService,
        GitHubReleasesClient gitHubReleasesClient,
        Action navigateBack)
    {
        _genService           = genService;
        _authService          = authService;
        _gitHubReleasesClient = gitHubReleasesClient;
        _navigateBack         = navigateBack;

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
            // stdout) — a step could go Active in the steps sidebar with nothing in the log to mark
            // the transition, and the next thing shown might be an unrelated-sounding line from that
            // step's own internal tooling (e.g. copype.cmd's own "Mounting ..." chatter), making the
            // log look out of sync with the sidebar. This is done here (once, at the ViewModel) rather
            // than in the service's ReportProgress, since that method also drives the IPC round-trip
            // for elevated generation (see BootImageGenerationService.TailProgress) — logging there
            // would double up every banner line during elevated runs. Step messages are kept short
            // and self-contained (no trailing "…") so they always fit the one-line progress header
            // on their own — the same text is reused verbatim as the banner line here.
            AppendLogLine(e.Message.TrimEnd());
        });

        _genService.LogMessage += (_, line) => OnUi(() => AppendLogLine(line));
        _genService.LogHeartbeat += (_, line) => OnUi(() => UpdateHeartbeatLine(line));

        GenerateCommand    = new RelayCommand(async _ => await GenerateAsync(), _ => CanGenerate);
        CancelCommand      = new RelayCommand(_ => Cancel(), _ => IsGenerating && !IsCancelling);
        BrowseCommand      = new RelayCommand(_ => BrowseLocalPath());
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutputFolder());
        BrowseDriverRootCommand = new RelayCommand(_ => BrowseDriverRoot());
        BackCommand        = new RelayCommand(_ => _navigateBack(), _ => !IsGenerating);
        NewGenerationCommand = new RelayCommand(_ => ResetToConfiguration(), _ => !IsGenerating);
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder(), _ => HasOutputWimPath);
    }

    public bool UseGitHubSource
    {
        get => _useGitHubSource;
        set { _useGitHubSource = value; UseLocalSource = !value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    public bool UseLocalSource
    {
        get => !_useGitHubSource;
        set { _useGitHubSource = !value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    public string LocalSourcePath
    {
        get => _localSourcePath;
        set { _localSourcePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

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
    /// support). Disables the Cancel button the instant it's pressed — clicking cancel a second
    /// time (or repeatedly) while cleanup is still in flight does nothing useful and just reads
    /// as unresponsive — and flips the spinner's rotation direction as a clear visual cue that a
    /// cancellation is in progress rather than the generation itself just running as normal.
    /// </summary>
    public bool IsCancelling
    {
        get => _isCancelling;
        private set { _isCancelling = value; OnPropertyChanged(); CommandManager.InvalidateRequerySuggested(); }
    }

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

    public int ProgressPercent
    {
        get => _progressPercent;
        private set { _progressPercent = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressPercentText)); }
    }

    /// <summary>Numeric percentage as display text (e.g. "42%") — the progress bar itself only shows the fill visually.</summary>
    public string ProgressPercentText => $"{ProgressPercent}%";

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

    public string? OutputWimPath
    {
        get => _outputWimPath;
        private set { _outputWimPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressSubtitle)); OnPropertyChanged(nameof(HasOutputWimPath)); }
    }

    /// <summary>Whether a finished WIM path is available to display/reveal (i.e. generation completed successfully).</summary>
    public bool HasOutputWimPath => OutputWimPath is not null;

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
        }
    }

    /// <summary>
    /// True once a generation has ended in failure or cancellation — the only cases worth
    /// offering a "start over" button for. A successful generation has nothing to retry, so
    /// that button is hidden once <see cref="IsComplete"/> is true (see GenerateBootImageView.xaml).
    /// </summary>
    public bool CanRetry => HasError || WasCancelled;

    public string GitHubStatus { get; } = "Latest release will be resolved automatically";

    /// <summary>
    /// Live command/output log streamed from the generation service (FR-051d), as one plain,
    /// selectable block of text (one line per entry, no wrapping) — bind a read-only TextBox
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
    /// Progress-view subheading. Carries the same information the old colored success/error/
    /// cancelled InfoBars used to (output path + upload hint, error detail, cancellation note)
    /// now that <see cref="ProgressTitle"/> and its icon convey the state itself.
    /// </summary>
    public string ProgressSubtitle =>
        IsComplete    ? "Boot image generated successfully. Upload the WIM to the Cloud Imaging Portal (Boot Images) to publish it." :
        HasError      ? ErrorMessage ?? "The boot image generation failed." :
        WasCancelled  ? "The boot image generation was cancelled and temporary files were cleaned up." :
        "Mounting the WinPE image and injecting the Cloud Imaging Client. This can take a few minutes.";

    public bool CanGenerate => !IsGenerating
        && !string.IsNullOrWhiteSpace(OutputFolderPath)
        && (!UseLocalSource || Directory.Exists(LocalSourcePath))
        && (string.IsNullOrWhiteSpace(DriverRootPath) || Directory.Exists(DriverRootPath));

    public ICommand GenerateCommand     { get; }
    public ICommand CancelCommand       { get; }
    public ICommand BrowseCommand       { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand BrowseDriverRootCommand { get; }
    public ICommand BackCommand         { get; }
    public ICommand NewGenerationCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }

    /// <summary>
    /// Opens File Explorer with the generated WIM pre-selected (same UX as
    /// PrepareStorageDeviceViewModel.OpenResultFolder), rather than requiring the technician to
    /// read/copy the full path from the subtitle text. Best-effort — a failure here is never
    /// fatal, since FR-051b's output path is still shown (selectable) below the log.
    /// </summary>
    private void OpenOutputFolder()
    {
        if (OutputWimPath is null)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{OutputWimPath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    private async Task GenerateAsync()
    {
        ClearLog();
        foreach (var step in Steps) step.State = GenerationStepState.Pending;
        OnPropertyChanged(nameof(StepProgressLabel));
        ProgressPercent = 0;
        ProgressMessage = "Starting";
        OutputWimPath   = null;
        IsGenerating  = true;
        IsCancelling  = false;
        IsComplete    = false;
        WasCancelled  = false;
        ErrorMessage  = null;

        _generationStartedAtUtc = DateTime.UtcNow;
        UpdateElapsedTimeText();
        _elapsedTimer.Start();

        _cts = new CancellationTokenSource();
        try
        {
            // Set (or refresh) the Operator API bearer token used for boot media certificate
            // retrieval, branding logo retrieval, and Device Gateway endpoint resolution —
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
                enableCommandPromptAccess: _enableCommandPromptAccess,
                ct: _cts.Token);

            OutputWimPath = result.WimPath;
            IsComplete    = true;
        }
        catch (OperationCanceledException)
        {
            WasCancelled    = true;
            ProgressMessage = "Generation cancelled.";
            AppendLogLine("Generation cancelled. Cleaning up (unmounting/discarding any in-progress WIM mount, deleting temp files).");
        }
        catch (Exception ex)
        {
            // Mirror the "FAILED: ..." convention already used for individual command failures
            // (RunExternalAsync) so the log always ends with a clear terminal marker line,
            // even when the failure happened somewhere that never itself called RaiseLog
            // (e.g. an ADK/prerequisite check) — otherwise the log would just trail off with
            // whatever step banner was last announced, with nothing to explain the red InfoBar
            // shown alongside it.
            // T160/FR-058: every failure path surfaces a support reference code so a technician
            // can quote it to support without needing log access.
            var code = SupportReferenceCode.ForMediaBuilder("GENBOOT", _currentStage);
            ErrorMessage = string.Create(CultureInfo.InvariantCulture, $"{ex.Message} (Error reference: {code})");
            AppendLogLine($"FAILED: {ErrorMessage}");
        }
        finally
        {
            IsGenerating = false;
            IsCancelling = false;
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
        // immediately, at the moment the user clicks Cancel — the later "Generation cancelled"
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
        void OnProgress(object? _, (string Message, int Percent) e) =>
            OnUi(() =>
            {
                ProgressMessage = e.Message;
                AppendLogLine(e.Message);
            });

        _gitHubReleasesClient.ProgressChanged += OnProgress;
        try
        {
            return await _gitHubReleasesClient.DownloadLatestClientAsync(_cts!.Token);
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

    /// <summary>Returns to the configuration view to start a fresh generation.</summary>
    private void ResetToConfiguration()
    {
        IsComplete   = false;
        WasCancelled = false;
        ErrorMessage = null;
        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        ClearLog();
        foreach (var step in Steps) step.State = GenerationStepState.Pending;
        OnPropertyChanged(nameof(StepProgressLabel));
    }

    /// <summary>
    /// Appends one clean line to <see cref="LogText"/>. External tools (copype.cmd, dism.exe)
    /// emit blank lines and decorative "===...===" banner lines mixed in with the actual
    /// step/status text — those are noise in a live activity log, so they're filtered out here
    /// rather than in the service (which still logs everything, unfiltered, via ILogger).
    /// </summary>
    private void AppendLogLine(string line)
    {
        var trimmed = line.TrimEnd();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.All(c => c is '=' or '-'))
            return;

        // A real line follows any pending heartbeat tick — the next heartbeat should start
        // a fresh line rather than overwrite this one.
        _heartbeatLineStart = -1;
        _logBuilder.Append(TimestampPrefix()).Append(trimmed).Append(Environment.NewLine);
        OnPropertyChanged(nameof(LogText));
    }

    /// <summary>
    /// Updates the trailing "still running (Ns elapsed)…" heartbeat line in place instead of
    /// appending a new one each tick, so a long-running, silent step (e.g. DISM mount/unmount)
    /// shows one line ticking over rather than flooding the log every few seconds.
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
        new("Mount WIM for customization", 30),
        new("Inject Cloud Imaging Client", 50),
        new(DriverInjectionStepLabel, 70),
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
                // The driver-injection step is optional (FR-051c) — when no driver root path was
                // given nothing was actually injected, so it reads as Skipped rather than Done.
                var noDriversToInject = step.Label == DriverInjectionStepLabel && string.IsNullOrWhiteSpace(_driverRootPath);
                step.State = noDriversToInject ? GenerationStepState.Skipped : GenerationStepState.Done;
            }
            else if (percent >= step.StartPercent)
                step.State = GenerationStepState.Active;
            else
                step.State = GenerationStepState.Pending;
        }
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
    Pending,
    Active,
    Done,

    /// <summary>Step was bypassed because its optional input was not provided (e.g. no drivers to inject).</summary>
    Skipped,
}

/// <summary>
/// One step of the boot image generation workflow, shown with live status in the progress
/// view. <see cref="StartPercent"/> is the overall-progress percentage at which the step
/// becomes active (see GenerateBootImageViewModel.UpdateSteps).
/// </summary>
public sealed class GenerationStep(string label, int startPercent) : INotifyPropertyChanged
{
    private GenerationStepState _state = GenerationStepState.Pending;

    public string Label { get; } = label;
    public int StartPercent { get; } = startPercent;

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
    public string? Caption => IsSkipped ? "Skipped (no drivers to inject)" : null;

    /// <summary>Symbol shown next to the step label: done ✓, active ▶, skipped –, pending ○.</summary>
    public string Glyph => _state switch
    {
        GenerationStepState.Done    => "\u2713", // ✓
        GenerationStepState.Active  => "\u25B6", // ▶
        GenerationStepState.Skipped => "\u2013", // –
        _                           => "\u25CB", // ○
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
