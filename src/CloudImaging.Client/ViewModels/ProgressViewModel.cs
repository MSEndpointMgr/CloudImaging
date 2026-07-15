using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.Client.ViewModels;

/// <summary>
/// View model for the ProgressView (T056, FR-007).
/// 3-node step indicator with overall progress bar.
/// </summary>
public sealed class ProgressViewModel : INotifyPropertyChanged
{
    private static readonly Brush PendingBrush   = new SolidColorBrush(Color.FromRgb(203, 213, 225)); // slate-300
    private static readonly Brush ActiveBrush    = new SolidColorBrush(Color.FromRgb( 37, 99, 235)); // blue-600
    private static readonly Brush CompletedBrush = new SolidColorBrush(Color.FromRgb( 22, 163, 74)); // green-600
    private static readonly Brush FailedBrush    = new SolidColorBrush(Color.FromRgb(220,  38, 38)); // red-600

    private int _overallPercent;
    private string _statusMessage = "Initialising…";
    private string? _errorMessage;
    private string? _supportReferenceCode;

    // Step states
    private ImagingStepStatus _formatStatus  = ImagingStepStatus.Pending;
    private ImagingStepStatus _downloadStatus = ImagingStepStatus.Pending;
    private ImagingStepStatus _applyStatus   = ImagingStepStatus.Pending;

    public int  OverallPercent { get => _overallPercent; set { _overallPercent = value; OnPropertyChanged(); } }
    public string StatusMessage  { get => _statusMessage;  set { _statusMessage  = value; OnPropertyChanged(); } }
    public string? ErrorMessage  { get => _errorMessage;   set { _errorMessage   = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); } }
    public string? SupportReferenceCode { get => _supportReferenceCode; set { _supportReferenceCode = value; OnPropertyChanged(); } }
    public bool HasError => ErrorMessage is not null;

    // Step completion booleans for XAML checkmark visibility
    public bool FormatCompleted   => _formatStatus   == ImagingStepStatus.Completed;
    public bool DownloadCompleted => _downloadStatus == ImagingStepStatus.Completed;
    public bool ApplyCompleted    => _applyStatus    == ImagingStepStatus.Completed;

    // Step node colors
    public Brush FormatStepColor   => StepBrush(_formatStatus);
    public Brush DownloadStepColor => StepBrush(_downloadStatus);
    public Brush ApplyStepColor    => StepBrush(_applyStatus);

    /// <summary>Updates a step's status and refreshes all step bindings.</summary>
    public void UpdateStep(ImagingStepName step, ImagingStepStatus status)
    {
        switch (step)
        {
            case ImagingStepName.FormatDisk:    _formatStatus   = status; break;
            case ImagingStepName.DownloadImage: _downloadStatus = status; break;
            case ImagingStepName.ApplyImage:    _applyStatus    = status; break;
        }
        NotifyStepBindings();
    }

    private void NotifyStepBindings()
    {
        OnPropertyChanged(nameof(FormatCompleted));
        OnPropertyChanged(nameof(DownloadCompleted));
        OnPropertyChanged(nameof(ApplyCompleted));
        OnPropertyChanged(nameof(FormatStepColor));
        OnPropertyChanged(nameof(DownloadStepColor));
        OnPropertyChanged(nameof(ApplyStepColor));
    }

    private static Brush StepBrush(ImagingStepStatus status) => status switch
    {
        ImagingStepStatus.InProgress => ActiveBrush,
        ImagingStepStatus.Completed  => CompletedBrush,
        ImagingStepStatus.Failed     => FailedBrush,
        _                            => PendingBrush,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
