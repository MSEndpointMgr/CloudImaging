using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CloudImaging.MediaBuilder.Services;
using Microsoft.Win32;

namespace CloudImaging.MediaBuilder.ViewModels;

/// <summary>
/// View model for the GenerateBootImageView (T153, FR-051a, FR-051b).
/// </summary>
public sealed class GenerateBootImageViewModel : INotifyPropertyChanged
{
    private readonly BootImageGenerationService _genService;
    private readonly EntraAuthenticationService _authService;
    private readonly Action _navigateBack;
    private readonly StringBuilder _logBuilder = new();

    private bool _useGitHubSource = true;
    private string _localSourcePath = string.Empty;
    private string _outputFolderPath = GetDefaultOutputFolder();
    private string _driverRootPath = string.Empty;
    private bool _isGenerating;
    private bool _isComplete;
    private int _progressPercent;
    private string _progressMessage = string.Empty;
    private string? _outputWimPath;
    private string? _errorMessage;

    public GenerateBootImageViewModel(
        BootImageGenerationService genService,
        EntraAuthenticationService authService,
        Action navigateBack)
    {
        _genService   = genService;
        _authService  = authService;
        _navigateBack = navigateBack;

        Steps = new ObservableCollection<GenerationStep>(CreateSteps());

        _genService.ProgressChanged += (_, e) => OnUi(() =>
        {
            ProgressMessage = e.Message;
            ProgressPercent = e.Percent;
            UpdateSteps(e.Percent);
        });

        _genService.LogMessage += (_, line) => OnUi(() => AppendLogLine(line));

        GenerateCommand    = new RelayCommand(async _ => await GenerateAsync(), _ => CanGenerate);
        BrowseCommand      = new RelayCommand(_ => BrowseLocalPath());
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutputFolder());
        BrowseDriverRootCommand = new RelayCommand(_ => BrowseDriverRoot());
        BackCommand        = new RelayCommand(_ => _navigateBack(), _ => !IsGenerating);
        NewGenerationCommand = new RelayCommand(_ => ResetToConfiguration(), _ => !IsGenerating);
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

    public bool IsGenerating
    {
        get => _isGenerating;
        private set { _isGenerating = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); OnPropertyChanged(nameof(ShowProgressView)); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool IsComplete
    {
        get => _isComplete;
        private set { _isComplete = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowProgressView)); }
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        private set { _progressPercent = value; OnPropertyChanged(); }
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set { _progressMessage = value; OnPropertyChanged(); }
    }

    public string? OutputWimPath
    {
        get => _outputWimPath;
        private set { _outputWimPath = value; OnPropertyChanged(); }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ShowProgressView)); }
    }

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

    /// <summary>
    /// True once generation has started (or finished / failed): the view swaps its
    /// configuration cards for the full progress + steps + log layout.
    /// </summary>
    public bool ShowProgressView => IsGenerating || IsComplete || HasError;

    public bool HasError    => ErrorMessage is not null;
    public bool CanGenerate => !IsGenerating
        && !string.IsNullOrWhiteSpace(OutputFolderPath)
        && (!UseLocalSource || Directory.Exists(LocalSourcePath))
        && (string.IsNullOrWhiteSpace(DriverRootPath) || Directory.Exists(DriverRootPath));

    public ICommand GenerateCommand     { get; }
    public ICommand BrowseCommand       { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand BrowseDriverRootCommand { get; }
    public ICommand BackCommand         { get; }
    public ICommand NewGenerationCommand { get; }

    private async Task GenerateAsync()
    {
        ClearLog();
        foreach (var step in Steps) step.State = GenerationStepState.Pending;
        ProgressPercent = 0;
        ProgressMessage = "Starting…";
        OutputWimPath   = null;
        IsGenerating = true;
        IsComplete   = false;
        ErrorMessage = null;

        try
        {
            string clientBinariesPath = UseGitHubSource
                ? await DownloadLatestClientAsync()
                : LocalSourcePath;

            // The generation service will try to embed the cert if the access token is available.
            // GenerateElevatedAsync transparently relaunches this app elevated (one UAC prompt)
            // if it isn't already running as Administrator, since DISM image mounting requires it.
            var result = await _genService.GenerateElevatedAsync(
                clientBinariesPath,
                pfxBytes: null,         // cert injection via T173 extension
                _outputFolderPath,
                driverRootPath: string.IsNullOrWhiteSpace(_driverRootPath) ? null : _driverRootPath);

            OutputWimPath = result.WimPath;
            IsComplete    = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private async Task<string> DownloadLatestClientAsync()
    {
        // Simplified: in full implementation this calls GitHub releases API.
        // For now, fallback to a temp directory placeholder.
        ProgressMessage = "Resolving latest GitHub release…";
        await Task.Delay(500); // placeholder
        return System.IO.Path.GetTempPath();
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
        ErrorMessage = null;
        ProgressPercent = 0;
        ProgressMessage = string.Empty;
        ClearLog();
        foreach (var step in Steps) step.State = GenerationStepState.Pending;
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

        _logBuilder.Append(trimmed).Append(Environment.NewLine);
        OnPropertyChanged(nameof(LogText));
    }

    private void ClearLog()
    {
        _logBuilder.Clear();
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
        new("Inject drivers (optional)", 70),
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

            if (percent >= 100 && i == Steps.Count - 1)
                step.State = GenerationStepState.Done;
            else if (percent >= nextStart)
                step.State = GenerationStepState.Done;
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
}

/// <summary>Lifecycle state of a single <see cref="GenerationStep"/>.</summary>
public enum GenerationStepState
{
    Pending,
    Active,
    Done,
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
        }
    }

    /// <summary>Symbol shown next to the step label: done ✓, active ▶, pending ○.</summary>
    public string Glyph => _state switch
    {
        GenerationStepState.Done   => "\u2713", // ✓
        GenerationStepState.Active => "\u25B6", // ▶
        _                          => "\u25CB", // ○
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
