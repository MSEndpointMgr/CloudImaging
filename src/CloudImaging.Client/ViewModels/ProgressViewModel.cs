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
/// a progress bar for the step currently running. The ticker and step captions surface
/// genuinely-available detail (the same <see cref="StatusMessage"/> strings
/// <see cref="ImagingWorkflowViewModel"/> already sets at each stage, plus wall-clock step
/// durations and, for transfers, the real downloaded/total byte counts reported by
/// <see cref="Services.ImageDownloadService"/>) rather than fabricated telemetry the pipeline
/// does not expose (e.g. transfer speed or time remaining).
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
    private int _stepPercent;
    private string? _transferDetail;
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

    /// <summary>
    /// Progress of the step currently running, 0-100, driving the bar at the bottom of
    /// ProgressView. Distinct from <see cref="OverallPercent"/>, which spans the whole pipeline
    /// and is shown over the ring: the bar answers "how far into this operation", the ring
    /// answers "how far into the session".
    ///
    /// <para>
    /// Only steps that can report genuine progress set this (image download, image apply,
    /// recovery image, and any future step such as driver injection). Steps that cannot — disk
    /// format, boot configuration — leave it at 0 rather than showing a fabricated value.
    /// <see cref="UpdateStep"/> resets it on every transition, so a new step always starts from
    /// empty and never inherits the previous step's fill.
    /// </para>
    /// </summary>
    public int StepPercent
    {
        get => _stepPercent;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            // The download loop reports far more often than the value actually changes; skipping
            // no-op notifications keeps this from queueing thousands of redundant UI updates.
            if (_stepPercent == clamped) return;
            _stepPercent = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Byte counter shown under the step progress bar while a transfer is running, e.g.
    /// "1.24 GB of 4.71 GB". Null whenever the current step is not transferring anything, which
    /// hides the caption entirely (see <see cref="HasTransferDetail"/>).
    /// </summary>
    public string? TransferDetail
    {
        get => _transferDetail;
        private set
        {
            if (_transferDetail == value) return;
            _transferDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTransferDetail));
        }
    }

    public bool HasTransferDetail => !string.IsNullOrEmpty(TransferDetail);

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

        // Every step transition clears the step-scoped bar and its byte counter, so a step that
        // reports no progress of its own (disk format, boot configuration) shows an empty bar
        // instead of inheriting the previous step's fill, and a step that does report starts
        // from zero. New steps added later get this behaviour without touching this method.
        ResetStepProgress();

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
    /// Empties the step progress bar and its byte counter. Called on every step transition, and
    /// directly by the pipeline when one step contains several phases (the recovery step
    /// downloads and then applies) so the bar restarts instead of sitting at 100% through the
    /// second phase.
    /// </summary>
    public void ResetStepProgress()
    {
        StepPercent = 0;
        TransferDetail = null;
    }

    /// <summary>
    /// Publishes the live byte counter for a transfer, e.g. "1.24 GB of 4.71 GB". Both figures
    /// use the unit chosen from the total, so the pair stays directly comparable instead of the
    /// left side switching from MB to GB partway through.
    /// </summary>
    /// <param name="transferredBytes">Bytes received so far.</param>
    /// <param name="totalBytes">Total size, or a non-positive value when the server did not send
    /// a Content-Length — in which case only the transferred amount is shown, since "of ?" is
    /// worse than no denominator at all.</param>
    public void SetTransferProgress(long transferredBytes, long totalBytes)
    {
        const double Mb = 1024d * 1024d;
        const double Gb = Mb * 1024d;

        var useGb = totalBytes > 0 ? totalBytes >= Gb : transferredBytes >= Gb;
        var unit  = useGb ? "GB" : "MB";
        var scale = useGb ? Gb : Mb;

        TransferDetail = totalBytes > 0
            ? $"{transferredBytes / scale:0.00} {unit} of {totalBytes / scale:0.00} {unit}"
            : $"{transferredBytes / scale:0.00} {unit} downloaded";
    }

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


