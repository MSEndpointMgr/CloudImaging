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
    public OperationSelectionView()
    {
        InitializeComponent();
        LoadBrandingLogo();
    }

    /// <summary>
    /// Opens the "Connect to Wi-Fi" support tool (FR-051d companion feature). Always available
    /// (unlike Command Prompt, not gated by any Media Builder opt-in) — a code-behind click
    /// handler, matching <c>MainWindow.ViewLogButton_Click</c>'s precedent, since opening a
    /// Window is a View-layer concern.
    /// </summary>
    private void ConnectWifiButton_Click(object sender, RoutedEventArgs e)
    {
        DialogHost.ShowDimmed(new WifiConnectionWindow(), Window.GetWindow(this));
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
