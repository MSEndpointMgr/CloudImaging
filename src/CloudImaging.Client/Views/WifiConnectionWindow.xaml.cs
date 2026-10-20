using System.ComponentModel;
using System.Windows;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;

namespace CloudImaging.Client.Views;

/// <summary>
/// "Connect to Wi-Fi" support tool window, opened from <see cref="OperationSelectionView"/>'s footer.
/// Unlike the Command Prompt support tool, it is not gated by the Media Builder opt-in toggle.
/// </summary>
public partial class WifiConnectionWindow : FluentWindow
{
    /// <summary>Builds the window and loads its XAML.</summary>
    /// <param name="managedWifi">The portal's Wi-Fi networks, listed first; null when there are none.</param>
    public WifiConnectionWindow(ManagedWifiService? managedWifi = null)
    {
        InitializeComponent();

        // See LogViewerWindow for why this is needed under WinPE (no full DWM composition).
        SourceInitialized += (_, _) =>
        {
            WindowStyle = WindowStyle.None;
            NativeWindowChromeFix.RemoveNativeCaption(this);
        };

        // Scan and connect failures belong in the Client log, which is the only diagnostic a technician can send.
        var logger = new Serilog.Extensions.Logging.SerilogLoggerFactory(Serilog.Log.Logger).CreateLogger<WirelessConnectionService>();
        var viewModel = new WifiConnectionViewModel(new WirelessConnectionService(logger), closeRequested: Close, managedWifi: managedWifi);
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
