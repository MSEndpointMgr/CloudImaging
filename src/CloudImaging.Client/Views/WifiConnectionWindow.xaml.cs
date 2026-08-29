using System.ComponentModel;
using System.Windows;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using Wpf.Ui.Controls;

namespace CloudImaging.Client.Views;

/// <summary>
/// "Connect to Wi-Fi" support tool window (companion to <see cref="MainWindow.ViewLogButton"/>'s
/// diagnostics access point). Opened from <see cref="OperationSelectionView"/>'s always-available
/// "Connect to Wi-Fi" button. Unlike the Command Prompt support tool, this is always available —
/// it is not gated by the Media Builder opt-in toggle.
/// </summary>
public partial class WifiConnectionWindow : FluentWindow
{
    public WifiConnectionWindow()
    {
        InitializeComponent();

        // See LogViewerWindow for why this is needed under WinPE (no full DWM composition).
        SourceInitialized += (_, _) =>
        {
            WindowStyle = WindowStyle.None;
            NativeWindowChromeFix.RemoveNativeCaption(this);
        };

        var viewModel = new WifiConnectionViewModel(new WirelessConnectionService(), closeRequested: Close);
        // The inline connect panel's PasswordBox is now a single persistent element (not
        // re-created per list item), so it must be cleared explicitly whenever the selected
        // network changes — otherwise a password typed for one network would silently carry
        // over into a Connect attempt against a different network picked afterwards.
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = viewModel;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WifiConnectionViewModel.SelectedNetwork))
            PasswordInput.Password = string.Empty;
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WifiConnectionViewModel vm)
            vm.ConnectCommand.Execute(PasswordInput.Password);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
