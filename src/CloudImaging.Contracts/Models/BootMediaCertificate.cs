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

    /// <summary>
    /// For a certificate replaced by a rotation: until when the Device Gateway still accepts it, so
    /// USB media built with it can update itself. Null or past means it is no longer accepted.
    /// </summary>
    public DateTimeOffset? AcceptedUntil { get; set; }

    /// <summary>Base64 DER of the public certificate (no private key), for encrypting to it.</summary>
    public string? CertificateDer { get; set; }
}

/// <summary>A boot media certificate the Device Gateway accepts right now.</summary>
/// <param name="Thumbprint">SHA-1 thumbprint (hex, uppercase).</param>
/// <param name="IsActive">True for the certificate new boot media is built with.</param>
/// <param name="AcceptedUntil">For the previous certificate, the end of its overlap; null for the active one.</param>
/// <param name="CertificateDer">Base64 DER of the public certificate.</param>
public sealed record AcceptedBootMediaCertificate(string Thumbprint, bool IsActive, DateTimeOffset? AcceptedUntil, string? CertificateDer);
