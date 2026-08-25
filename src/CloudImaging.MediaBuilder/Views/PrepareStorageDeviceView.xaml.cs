using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CloudImaging.MediaBuilder.Views;

public partial class PrepareStorageDeviceView : UserControl
{
    public PrepareStorageDeviceView()
    {
        InitializeComponent();

        // Progress lives in its own card near the bottom of the (potentially long) scrollable
        // form, so simply making it visible when a Prepare/Generate ISO operation starts isn't
        // enough — it can land below the fold with no visual cue that anything happened. Auto
        // -scroll to the bottom whenever IsBusy flips on so the progress bar is immediately in
        // view (mirrors GenerateBootImageView's LogTextBox auto-scroll-on-change).
        //
        // Looked up via reflection (rather than a direct PrepareStorageDeviceViewModel
        // reference) because this file is also compiled by WPF's temporary markup-compilation
        // project, which — per FixWpfTempProjectSourceGenerators in Directory.Build.targets —
        // excludes the ViewModels folder to avoid duplicate [LoggerMessage] source-generator
        // output; a direct type reference here would fail to resolve in that temp project.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged oldVm)
                oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            if (e.NewValue is INotifyPropertyChanged newVm)
                newVm.PropertyChanged += OnViewModelPropertyChanged;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "IsBusy")
            return;
        if (sender?.GetType().GetProperty("IsBusy")?.GetValue(sender) is not true)
            return;

        // Deferred to ContextIdle so the progress card's Visibility change has already been
        // measured/arranged (and the ScrollViewer's extent updated) by the time ScrollToEnd runs.
        Dispatcher.InvokeAsync(() => ContentScrollViewer.ScrollToEnd(), DispatcherPriority.ContextIdle);
    }
}
