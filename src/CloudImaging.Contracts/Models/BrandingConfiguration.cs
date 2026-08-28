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

    // Surface background colours (FR-038 extension). Each portal surface has an independent
    // light- and dark-theme value since the neutral (non-brand) shadcn theme differs
    // significantly between the two, and a single value risks poor contrast in one of them.
    // Defaults mirror the built-in shadcn "slate" theme already baked into index.css.
    public string SidebarBackgroundLight { get; init; } = "#f8fafc";
    public string SidebarBackgroundDark { get; init; } = "#0d1321";
    public string CardBackgroundLight { get; init; } = "#ffffff";
    public string CardBackgroundDark { get; init; } = "#0c121f";
    public string PageBackgroundLight { get; init; } = "#ffffff";
    public string PageBackgroundDark { get; init; } = "#080c16";
    public string HeaderBackgroundLight { get; init; } = "#ffffff";
    public string HeaderBackgroundDark { get; init; } = "#080c16";
}
