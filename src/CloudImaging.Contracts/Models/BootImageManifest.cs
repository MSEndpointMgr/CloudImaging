namespace CloudImaging.Contracts.Models;

/// <summary>
/// Integrity/provenance manifest embedded inside a generated boot image WIM at
/// <c>\ci-manifest.json</c> (FR-051).
///
/// This is deliberately NOT a cryptographic signature. Boot image signing (T065) was
/// descoped after the Generate Boot Image workflow was found to have no genuine threat
/// model requiring non-repudiation: the only actor that ever consumes a boot image is the
/// Media Builder's own Prepare USB Storage Device workflow, which already performs an
/// independent SHA256 hash check against the value the Operator API returns for the
/// selected catalog entry before deploying to USB (FR-056) — that check is the real
/// integrity/tamper boundary. This manifest exists purely to record build-time provenance
/// (component checksums, driver injection counts, etc.) for diagnostics.
/// </summary>
public sealed class BootImageManifest
{
    public const string ManifestSchemaVersion = "1.0";

    public required string ManifestVersion { get; init; }
    public required string ImageVersion { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? WinPeVersion { get; init; }
    public string? ClientVersion { get; init; }

    /// <summary>SHA-256 hex hash per embedded component (e.g. "cloudImagingClient", "brandingLogo", "bootMediaCertificate").</summary>
    public Dictionary<string, string> ComponentChecksums { get; init; } = [];

    /// <summary>Additional deployment metadata (e.g. driver packages injected, ADK path used).</summary>
    public Dictionary<string, object> DeploymentMetadata { get; init; } = [];
}
