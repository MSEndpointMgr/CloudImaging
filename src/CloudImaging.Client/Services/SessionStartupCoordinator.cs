using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Coordinates the application startup sequence (T034, FR-071).
/// Loads the mTLS boot-media certificate PFX from the executable directory,
/// configures the <see cref="HttpClientHandler"/> for Device Gateway API calls,
/// and reports errors if the certificate cannot be loaded.
/// </summary>
public sealed partial class SessionStartupCoordinator
{
    /// <summary>
    /// Path of the PFX relative to the Cloud Imaging Client executable directory (FR-071).
    /// Embedded in the boot image WIM during the Generate Boot Image workflow.
    /// </summary>
    public const string PfxRelativePath = @"certificates\bootmedia.pfx";

    private readonly ILogger<SessionStartupCoordinator> _logger;

    public SessionStartupCoordinator(ILogger<SessionStartupCoordinator> logger)
        => _logger = logger;

    /// <summary>
    /// Result of the startup certificate check.
    /// </summary>
    /// <param name="Success">Whether the boot-media certificate was loaded successfully.</param>
    /// <param name="ErrorMessage">User-facing error message when <paramref name="Success"/> is false.</param>
    /// <param name="Certificate">
    /// The loaded boot-media certificate (with private key) on success; used to sign the
    /// session-bootstrap proof-of-possession (FR-069). Null on failure.
    /// </param>
    public sealed record StartupResult(bool Success, string? ErrorMessage, X509Certificate2? Certificate = null);

    /// <summary>
    /// Attempts to load the boot-media certificate PFX and configure it on the
    /// <paramref name="handler"/> for mTLS authentication to the Device Gateway API.
    /// </summary>
    public StartupResult ConfigureMtlsCertificate(HttpClientHandler handler)
    {
        var exeDir  = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        var pfxPath = Path.Combine(exeDir, PfxRelativePath);

        if (!File.Exists(pfxPath))
        {
            LogPfxNotFound(_logger, pfxPath);
            return new StartupResult(false,
                "Boot media certificate not found. " +
                "Please regenerate the boot image using Cloud Imaging Media Builder " +
                "and re-prepare USB media.");
        }

        try
        {
            // Load PFX without password — the boot-media cert is exported without password (FR-071)
            var cert = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
            handler.ClientCertificates.Add(cert);
            LogPfxLoaded(_logger, cert.Thumbprint);
            return new StartupResult(true, null, cert);
        }
        catch (Exception ex)
        {
            LogPfxLoadError(_logger, ex);
            return new StartupResult(false,
                "Boot media certificate could not be loaded. " +
                "Please regenerate the boot image and re-prepare USB media.");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Boot media PFX not found at '{Path}'.")]
    private static partial void LogPfxNotFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot media PFX loaded successfully. Thumbprint={Thumbprint}")]
    private static partial void LogPfxLoaded(ILogger logger, string thumbprint);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load boot media PFX.")]
    private static partial void LogPfxLoadError(ILogger logger, Exception ex);
}
