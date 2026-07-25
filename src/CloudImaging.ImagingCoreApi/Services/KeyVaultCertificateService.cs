using Azure.Security.KeyVault.Secrets;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Stores and retrieves boot-media certificate PFX bytes from Azure Key Vault (FR-068).
///
/// Secret naming convention: <c>boot-media-cert-{thumbprintPrefix8}</c>
/// where <c>thumbprintPrefix8</c> is the first 8 hex characters of the SHA-1 thumbprint.
/// The full thumbprint is stored as a secret tag for disambiguation.
/// </summary>
public sealed partial class KeyVaultCertificateService
{
    private const string SecretNamePrefix = "boot-media-cert-";

    private readonly SecretClient _kvClient;
    private readonly ILogger<KeyVaultCertificateService> _logger;

    public KeyVaultCertificateService(
        SecretClient kvClient,
        ILogger<KeyVaultCertificateService> logger)
    {
        _kvClient = kvClient;
        _logger = logger;
    }

    /// <summary>
    /// Stores the PFX bytes in Key Vault and returns the secret name that was used.
    /// </summary>
    /// <param name="thumbprint">SHA-1 thumbprint of the certificate (hex, uppercase).</param>
    /// <param name="pfxBytes">Raw PFX bytes (PKCS#12 format, may be password-protected).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Key Vault secret name used for storage.</returns>
    public async Task<string> StorePfxAsync(string thumbprint, byte[] pfxBytes, CancellationToken ct = default)
    {
        string secretName = BuildSecretName(thumbprint);
        string pfxBase64 = Convert.ToBase64String(pfxBytes);

        var secret = new KeyVaultSecret(secretName, pfxBase64)
        {
            Properties =
            {
                ContentType = "application/x-pkcs12",
            },
        };
        secret.Properties.Tags["Thumbprint"] = thumbprint;

        await _kvClient.SetSecretAsync(secret, ct);
        LogStoredPfx(_logger, secretName);
        return secretName;
    }

    /// <summary>
    /// Retrieves the PFX bytes for the specified secret name from Key Vault.
    /// </summary>
    /// <param name="secretName">Key Vault secret name returned by <see cref="StorePfxAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Raw PFX bytes.</returns>
    public async Task<byte[]> RetrievePfxAsync(string secretName, CancellationToken ct = default)
    {
        var response = await _kvClient.GetSecretAsync(secretName, cancellationToken: ct);
        LogRetrievedPfx(_logger, secretName);
        return Convert.FromBase64String(response.Value.Value);
    }

    // ------------------------------------------------------------------ helpers

    private static string BuildSecretName(string thumbprint)
    {
        // Key Vault secret names must match ^[0-9a-zA-Z-]+$ — use lower-hex prefix
        string prefix = thumbprint[..Math.Min(8, thumbprint.Length)].ToLowerInvariant();
        return $"{SecretNamePrefix}{prefix}";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored boot media PFX in Key Vault: secret={SecretName}")]
    private static partial void LogStoredPfx(ILogger logger, string secretName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Retrieved boot media PFX from Key Vault: secret={SecretName}")]
    private static partial void LogRetrievedPfx(ILogger logger, string secretName);
}
