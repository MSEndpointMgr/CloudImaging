namespace CloudImaging.Contracts.Models;

/// <summary>Portal branding settings (Key Entities, FR-038).</summary>
public sealed class BrandingConfiguration
{
    /// <summary>
    /// Blob path of the <b>boot image</b> logo embedded into boot media by the Media Builder.
    /// Served to the Media Builder via a time-limited SAS URL (FR-062).
    /// </summary>
    public string? LogoBlobPath { get; init; }

    /// <summary>
    /// Blob path of the <b>portal</b> logo shown in the portal header/sidebar. Streamed to the
    /// browser through the portal backend (no SAS). Typically smaller than the boot image logo.
    /// </summary>
    public string? PortalLogoBlobPath { get; init; }

    public string PrimaryColor { get; init; } = "#0078d4";
    public string AccentColor { get; init; } = "#005a9e";
    public string ApplicationName { get; init; } = "Cloud Imaging";
}
