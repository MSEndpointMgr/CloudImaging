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

        DataContext = new WifiConnectionViewModel(new WirelessConnectionService(), closeRequested: Close);
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WifiConnectionViewModel vm)
            vm.ConnectCommand.Execute(PasswordInput.Password);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
