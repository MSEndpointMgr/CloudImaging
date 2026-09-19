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

    /// <summary>Primary brand color used across the portal.</summary>
    public string PrimaryColor { get; init; } = "#0078d4";

    /// <summary>Accent brand color used for highlights and focus elements.</summary>
    public string AccentColor { get; init; } = "#005a9e";

    /// <summary>Branded application name shown in the portal header and title.</summary>
    public string ApplicationName { get; init; } = "Cloud Imaging";

    // Surface background colours (FR-038 extension). Each portal surface has an independent
    // light- and dark-theme value since the neutral (non-brand) shadcn theme differs
    // significantly between the two, and a single value risks poor contrast in one of them.
    // Defaults mirror the built-in shadcn "slate" theme already baked into index.css.

    /// <summary>Sidebar background in light theme.</summary>
    public string SidebarBackgroundLight { get; init; } = "#f8fafc";

    /// <summary>Sidebar background in dark theme.</summary>
    public string SidebarBackgroundDark { get; init; } = "#0d1321";

    /// <summary>Card surface background in light theme.</summary>
    public string CardBackgroundLight { get; init; } = "#ffffff";

    /// <summary>Card surface background in dark theme.</summary>
    public string CardBackgroundDark { get; init; } = "#0c121f";

    /// <summary>Page background in light theme.</summary>
    public string PageBackgroundLight { get; init; } = "#ffffff";

    /// <summary>Page background in dark theme.</summary>
    public string PageBackgroundDark { get; init; } = "#080c16";

    /// <summary>Header background in light theme.</summary>
    public string HeaderBackgroundLight { get; init; } = "#ffffff";

    /// <summary>Header background in dark theme.</summary>
    public string HeaderBackgroundDark { get; init; } = "#080c16";
}
