using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace CloudImaging.MediaBuilder.Views;

public partial class MainWindow : FluentWindow
{
    /// <summary>Builds the window and loads its XAML.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // Follow the OS light/dark theme. Apply the current system theme up front so
        // the app adheres to the OS setting at launch (SystemThemeWatcher only reacts
        // to later *changes*, not the initial state). Guarded so a failure (e.g. under
        // elevation) never blocks startup — theme following is cosmetic.
        try
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
            Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        }
        catch (Exception)
        {
            // Keep the default theme.
        }
    }

    /// <summary>Swaps the hosted view (a UserControl) shown below the title bar.</summary>
    public void NavigateTo(System.Windows.Controls.Control view)
    {
        ContentHost.Content = view;
    }

    private IOperationHost? _closingShell;
    private bool _closeConfirmed;

    private IOperationHost? RunningShell =>
        ContentHost.Content is ShellView { DataContext: IOperationHost { IsOperationRunning: true } shell } ? shell : null;

    /// <summary>Asks before closing while an operation runs, so mounts and temp files are never abandoned.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeConfirmed && RunningShell is { } shell)
        {
            e.Cancel = true;
            if (_closingShell is null)
                ShowCloseConfirmation(shell);
            return;
        }

        base.OnClosing(e);
    }

    private void ShowCloseConfirmation(IOperationHost shell)
    {
        CloseGuardTitle.Text = $"{shell.RunningOperationName} is running";
        CloseGuardMessage.Text = shell.RunningOperationCloseWarning;
        CloseGuardButtons.Visibility = Visibility.Visible;
        CloseGuardProgress.Visibility = Visibility.Collapsed;
        CloseGuardOverlay.Visibility = Visibility.Visible;
    }

    private void OnKeepRunningClick(object sender, RoutedEventArgs e) =>
        CloseGuardOverlay.Visibility = Visibility.Collapsed;

    private void OnCancelAndCloseClick(object sender, RoutedEventArgs e)
    {
        if (RunningShell is not { } shell)
        {
            CloseNow();
            return;
        }

        _closingShell = shell;
        CloseGuardTitle.Text = $"Cancelling {shell.RunningOperationName.ToLowerInvariant()}";
        CloseGuardMessage.Text = "The window closes once cleanup has finished.";
        CloseGuardButtons.Visibility = Visibility.Collapsed;
        CloseGuardProgress.Visibility = Visibility.Visible;

        shell.PropertyChanged += OnClosingShellPropertyChanged;
        shell.CancelRunningOperation();
    }

    private void OnClosingShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IOperationHost.IsOperationRunning) && _closingShell is { IsOperationRunning: false })
            Dispatcher.BeginInvoke(CloseNow);
    }

    private void CloseNow()
    {
        if (_closingShell is not null)
            _closingShell.PropertyChanged -= OnClosingShellPropertyChanged;
        _closeConfirmed = true;
        Close();
    }
}
