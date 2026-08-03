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
    /// Shows the portal-provided branding logo embedded in the boot image
    /// (<c>branding\logo.png</c> next to the exe). When no logo was configured
    /// through the portal, the image stays collapsed so nothing is shown.
    /// </summary>
    private void LoadBrandingLogo()
    {
        var logoPath = new BrandingLogoService(NullLogger<BrandingLogoService>.Instance).GetLogoPath();
        if (logoPath is null)
        {
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
            // A corrupt or unreadable logo must never block operation selection.
        }
    }
}
