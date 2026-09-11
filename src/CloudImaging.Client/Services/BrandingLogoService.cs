using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Loads the branding logo embedded in the boot image WIM at
/// <c>&lt;exe directory&gt;\branding\logo.png</c> (FR-002a).
/// Falls back to the MSEndpointMgr default if no asset is found.
/// </summary>
public sealed partial class BrandingLogoService
{
    private const string LogoRelativePath = "branding\\logo.png";
    private readonly ILogger<BrandingLogoService> _logger;

    public BrandingLogoService(ILogger<BrandingLogoService> logger) => _logger = logger;

    /// <summary>
    /// Returns the full path to the logo to display.
    /// If the boot-image-embedded logo is not found, returns <c>null</c>
    /// (callers should fall back to the default MSEndpointMgr logo resource).
    /// </summary>
    public string? GetLogoPath()
    {
        var exeDir = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location) ?? string.Empty;

        var logoPath = Path.Combine(exeDir, LogoRelativePath);

        if (File.Exists(logoPath))
        {
            LogLogoFound(_logger, logoPath);
            return logoPath;
        }

        LogLogoFallback(_logger, logoPath);
        return null;
    }

    /// <summary>
    /// Loads the configured logo fully into memory so the embedded file is not held open.
    /// Returns <c>null</c> when no custom logo exists or the file is unreadable/corrupt.
    /// </summary>
    public BitmapImage? LoadLogo()
    {
        var logoPath = GetLogoPath();
        if (logoPath is null)
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Custom branding logo found: {Path}")]
    private static partial void LogLogoFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "No custom branding logo at '{Path}' — using default MSEndpointMgr logo.")]
    private static partial void LogLogoFallback(ILogger logger, string path);
}
