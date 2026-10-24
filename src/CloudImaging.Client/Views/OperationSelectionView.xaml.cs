using System.Windows;
using System.Windows.Controls;
using CloudImaging.Client.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.Views;

/// <summary>
/// Operation selection screen. Hosted by <see cref="MainWindow"/>'s navigation frame.
/// </summary>
public partial class OperationSelectionView : Page
{
    /// <summary>Builds the view and loads its XAML.</summary>
    public OperationSelectionView()
    {
        InitializeComponent();
        LoadBrandingLogo();
        Loaded += (_, _) => (DataContext as ViewModels.OperationSelectionViewModel)?.StartWatchingNetwork();
        Unloaded += (_, _) => (DataContext as ViewModels.OperationSelectionViewModel)?.StopWatchingNetwork();
    }

    /// <summary>Opens the Network panel above the footer button, with a fresh read of the adapters.</summary>
    private void NetworkButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.OperationSelectionViewModel vm)
            _ = vm.RefreshNetworkAsync();
        NetworkPopup.IsOpen = true;
    }

    private void PanelCheckAgainButton_Click(object sender, RoutedEventArgs e)
    {
        NetworkPopup.IsOpen = false;
        if (DataContext is ViewModels.OperationSelectionViewModel vm && vm.RecheckCommand.CanExecute(null))
            vm.RecheckCommand.Execute(null);
    }

    /// <summary>
    /// Opens the "Connect to Wi-Fi" support tool, from the message card or the Network panel. Only
    /// offered on a Windows RE boot image (Windows PE has no Wi-Fi); opening a Window is a View-layer
    /// concern, so this is a code-behind click handler.
    /// </summary>
    private void ConnectWifiButton_Click(object sender, RoutedEventArgs e)
    {
        NetworkPopup.IsOpen = false;
        var wifiWindow = new WifiConnectionWindow((DataContext as ViewModels.OperationSelectionViewModel)?.ManagedWifi);
        DialogHost.ShowDimmed(wifiWindow, Window.GetWindow(this));
        if (DataContext is not ViewModels.OperationSelectionViewModel vm)
            return;

        // A new Wi-Fi connection may be what was missing, so check again instead of making the technician ask.
        if (wifiWindow.DataContext is ViewModels.WifiConnectionViewModel { HasConnected: true } && vm.IsServiceBlocked && vm.RecheckCommand.CanExecute(null))
            vm.RecheckCommand.Execute(null);
        else
            _ = vm.RefreshNetworkAsync();
    }

    /// <summary>
    /// Shows the portal-provided branding logo embedded in the boot image
    /// (<c>branding\logo.png</c> next to the exe). When no logo was configured through the
    /// portal, a default vector logo (<see cref="OperationSelectionView.BrandingLogoFallback"/>,
    /// a WPF-UI symbol glyph rather than a raster asset) is shown instead, so the header never
    /// renders with a blank gap.
    /// </summary>
    private void LoadBrandingLogo()
    {
        var logo = new BrandingLogoService(NullLogger<BrandingLogoService>.Instance).LoadLogo();
        if (logo is null)
        {
            BrandingLogoFallback.Visibility = Visibility.Visible;
            return;
        }

        BrandingLogo.Source = logo;
        BrandingLogo.Visibility = Visibility.Visible;
    }
}
