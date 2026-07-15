namespace CloudImaging.Contracts.Models;

/// <summary>
/// Represents the metadata record for a boot media certificate stored in Table Storage.
/// The PFX bytes are stored separately in Azure Key Vault (FR-068).
/// </summary>
public sealed class BootMediaCertificate
{
    /// <summary>SHA-1 certificate thumbprint (hex, uppercase, no colons).</summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>Certificate validity start (UTC).</summary>
    public DateTimeOffset NotBefore { get; set; }

    /// <summary>Certificate expiry (UTC).</summary>
    public DateTimeOffset NotAfter { get; set; }

    /// <summary>
    /// Whether this certificate is the currently active mTLS certificate.
    /// Only one row should have IsActive=true at any given time.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Azure Key Vault secret name under which the PFX bytes are stored.
    /// The secret name is of the form <c>boot-media-cert-{thumbprint-prefix}</c>.
    /// </summary>
    public string KeyVaultSecretName { get; set; } = string.Empty;
}
