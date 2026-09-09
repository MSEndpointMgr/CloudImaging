using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
        var logoPath = new BrandingLogoService(NullLogger<BrandingLogoService>.Instance).GetLogoPath();
        if (logoPath is null)
        {
            BrandingLogoFallback.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad; // load fully so the file handle is released
            bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            BrandingLogo.Source = bitmap;
            BrandingLogo.Visibility = Visibility.Visible;
        }
        catch
        {
            // A corrupt or unreadable logo must never block operation selection —
            // fall back to the default vector logo instead.
            BrandingLogoFallback.Visibility = Visibility.Visible;
        }
    }
}
