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
    /// <summary>Schema version of this manifest file. Currently "1.0".</summary>
    public const string ManifestSchemaVersion = "1.0";

    /// <summary>Version of the boot image build process.</summary>
    public required string ManifestVersion { get; init; }

    /// <summary>Version of the generated boot image this manifest describes.</summary>
    public required string ImageVersion { get; init; }

    /// <summary>UTC timestamp when this manifest was generated.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Version of the Windows Preinstallation Environment (WinPE) used.</summary>
    public string? WinPeVersion { get; init; }

    /// <summary>Version of the embedded Cloud Imaging Client, if injected.</summary>
    public string? ClientVersion { get; init; }

    /// <summary>SHA-256 hex hash per embedded component (e.g. "cloudImagingClient", "brandingLogo", "bootMediaCertificate").</summary>
    public Dictionary<string, string> ComponentChecksums { get; init; } = [];

    /// <summary>Additional deployment metadata (e.g. driver packages injected, ADK path used).</summary>
    public Dictionary<string, object> DeploymentMetadata { get; init; } = [];

    /// <summary>
    /// Whether this boot image was built with the Media Builder's "Enable command prompt
    /// access" opt-in checked (FR-051d) — provenance/audit visibility for whoever inspects
    /// this manifest later, independent of the Client's own <c>appsettings.json</c> stamp.
    /// </summary>
    public bool SupportToolsEnabled { get; init; }
}
