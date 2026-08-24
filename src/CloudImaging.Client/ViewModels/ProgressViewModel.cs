using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.Client.ViewModels;

/// <summary>Visual state of a single node in <see cref="ProgressViewModel.Steps"/>.</summary>
public enum ProgressStepState
{
    Pending,
    Active,
    Done,
    Failed
}

/// <summary>
/// One entry in the ProgressView's step sidebar (Format Disk, Download Image, ...): a fixed
/// label, a visual <see cref="State"/> driving the sidebar's dot/label styling via XAML
/// DataTriggers, and an optional short <see cref="Caption"/> ("In progress…", "Completed in
/// 12s", "Failed") — mirrors the Steps sidebar pattern in
/// CloudImaging.MediaBuilder's GenerateBootImageView.
/// </summary>
public sealed class ProgressStepItem : INotifyPropertyChanged
{
    private ProgressStepState _state = ProgressStepState.Pending;
    private string? _caption;

    internal ProgressStepItem(ImagingStepName step, string label)
    {
        Step = step;
        Label = label;
    }

    public ImagingStepName Step { get; }
    public string Label { get; }

    public ProgressStepState State
    {
        get => _state;
        internal set { _state = value; OnPropertyChanged(); }
    }

    public string? Caption
    {
        get => _caption;
        internal set { _caption = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// View model for the ProgressView (T056, FR-007): a step sidebar (Format, Download, Apply,
/// Configure Boot, Apply Recovery) alongside a progress ring, a short live-activity ticker, and
/// an overall progress bar. The ticker and step captions surface genuinely-available detail
/// (the same <see cref="StatusMessage"/> strings <see cref="ImagingWorkflowViewModel"/> already
/// sets at each stage, plus wall-clock step durations) rather than fabricated telemetry the
/// pipeline does not currently expose (e.g. byte-level transfer speed).
/// </summary>
public sealed class ProgressViewModel : INotifyPropertyChanged
{
    /// <summary>Caps how many recent activity lines <see cref="LogText"/> keeps. The ticker is
    /// scrollable, so this is generous enough to retain most of a session's step/command
    /// history rather than just the last couple of lines.</summary>
    private const int MaxActivityLines = 30;

    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly Dictionary<ImagingStepName, DateTime> _stepStartedAtUtc = new();
    private readonly List<string> _activityLines = new();

    private int _overallPercent;
    private string _statusMessage = "Initialising…";
    private string? _errorMessage;
    private string? _supportReferenceCode;
    private string _logText = string.Empty;

    public ProgressViewModel()
    {
        Steps = new ObservableCollection<ProgressStepItem>
        {
            new(ImagingStepName.FormatDisk,         "Format Disk"),
            new(ImagingStepName.DownloadImage,      "Download Image"),
            new(ImagingStepName.ApplyImage,         "Apply Image"),
            new(ImagingStepName.ConfigureBoot,      "Configure Boot"),
            new(ImagingStepName.ApplyRecoveryImage, "Apply Recovery Image"),
        };
    }

    /// <summary>The 5-node step pipeline shown in ProgressView's sidebar.</summary>
    public ObservableCollection<ProgressStepItem> Steps { get; }

    public int OverallPercent
    {
        get => _overallPercent;
        set
        {
            _overallPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(StepSubtitle));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value;
            OnPropertyChanged();
            AppendActivity(value);
        }
    }

    public string? ErrorMessage  { get => _errorMessage;   set { _errorMessage   = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); } }
    public string? SupportReferenceCode { get => _supportReferenceCode; set { _supportReferenceCode = value; OnPropertyChanged(); } }
    public bool HasError => ErrorMessage is not null;

    /// <summary>The last few <see cref="StatusMessage"/> updates, each timestamped, newline-joined
    /// for direct binding to a read-only TextBox "ticker" (FR-007: light live detail).</summary>
    public string LogText { get => _logText; private set { _logText = value; OnPropertyChanged(); } }

    /// <summary>Short label of the step currently in progress — falls back to the most recently
    /// failed, then most recently completed, step so the heading is never blank.</summary>
    public string CurrentStepTitle =>
        Steps.FirstOrDefault(s => s.State == ProgressStepState.Active)?.Label
        ?? Steps.LastOrDefault(s => s.State == ProgressStepState.Failed)?.Label
        ?? Steps.LastOrDefault(s => s.State == ProgressStepState.Done)?.Label
        ?? Steps[0].Label;

    /// <summary>"Step X of 5 · Elapsed mm:ss" shown under the title.</summary>
    public string StepSubtitle => $"Step {CurrentStepNumber} of {Steps.Count} · Elapsed {ElapsedText}";

    /// <summary>Wall-clock time since this view model was created (i.e. since imaging started),
    /// refreshed opportunistically whenever progress/step bindings change.</summary>
    public string ElapsedText
    {
        get
        {
            var elapsed = DateTime.UtcNow - _startedAtUtc;
            return elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
                : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }
    }

    private int CurrentStepNumber
    {
        get
        {
            var activeIndex = Steps.ToList().FindIndex(s => s.State == ProgressStepState.Active);
            if (activeIndex >= 0) return activeIndex + 1;

            var lastDoneIndex = Steps.ToList().FindLastIndex(s => s.State == ProgressStepState.Done);
            return lastDoneIndex >= 0 ? Math.Min(lastDoneIndex + 2, Steps.Count) : 1;
        }
    }

    /// <summary>Updates a step's status, refreshes its sidebar entry (state + duration caption),
    /// and re-derives the header title/subtitle.</summary>
    public void UpdateStep(ImagingStepName step, ImagingStepStatus status)
    {
        var item = Steps.First(s => s.Step == step);

        item.State = status switch
        {
            ImagingStepStatus.InProgress => ProgressStepState.Active,
            ImagingStepStatus.Completed  => ProgressStepState.Done,
            ImagingStepStatus.Failed     => ProgressStepState.Failed,
            _                            => ProgressStepState.Pending,
        };

        item.Caption = item.State switch
        {
            ProgressStepState.Active => Track(step),
            ProgressStepState.Done   => Completed(step),
            ProgressStepState.Failed => "Failed",
            _                        => null,
        };

        OnPropertyChanged(nameof(CurrentStepTitle));
        OnPropertyChanged(nameof(StepSubtitle));

        string Track(ImagingStepName s) { _stepStartedAtUtc[s] = DateTime.UtcNow; return "In progress…"; }
        string Completed(ImagingStepName s) => _stepStartedAtUtc.TryGetValue(s, out var startedAt)
            ? $"Completed in {FormatDuration(DateTime.UtcNow - startedAt)}"
            : "Completed";
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{Math.Max(1, (int)d.TotalSeconds)}s";

    /// <summary>
    /// Appends a single timestamped line to <see cref="LogText"/>. Called both for each
    /// phase-level <see cref="StatusMessage"/> change and — via
    /// <see cref="Services.ProgressActivityLoggerFactory"/> — for every Information-level log
    /// message the pipeline's step services (DiskFormatService, ImageDownloadService,
    /// ImageApplyService, BootConfigurationService, RecoveryImageService) already emit, so the
    /// ticker shows the real commands/sub-steps each phase runs rather than just one line per
    /// phase transition.
    /// </summary>
    public void AppendActivity(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        _activityLines.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (_activityLines.Count > MaxActivityLines) _activityLines.RemoveAt(0);

        LogText = string.Join('\n', _activityLines);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


