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
    }

    /// <summary>
    /// Opens the "Connect to Wi-Fi" support tool. Shown only on a Windows RE boot image (Windows PE
    /// has no Wi-Fi); opening a Window is a View-layer concern, so this is a code-behind click handler.
    /// </summary>
    private void ConnectWifiButton_Click(object sender, RoutedEventArgs e)
    {
        var wifiWindow = new WifiConnectionWindow((DataContext as ViewModels.OperationSelectionViewModel)?.ManagedWifi);
        DialogHost.ShowDimmed(wifiWindow, Window.GetWindow(this));

        // A new Wi-Fi connection may be what was missing, so check again instead of making the technician ask.
        if (wifiWindow.DataContext is ViewModels.WifiConnectionViewModel { HasConnected: true }
            && DataContext is ViewModels.OperationSelectionViewModel { IsServiceBlocked: true } vm
            && vm.RecheckCommand.CanExecute(null))
        {
            vm.RecheckCommand.Execute(null);
        }
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
