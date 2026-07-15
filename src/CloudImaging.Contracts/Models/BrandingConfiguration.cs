namespace CloudImaging.Contracts.Models;

/// <summary>Portal branding settings (Key Entities, FR-038).</summary>
public sealed class BrandingConfiguration
{
    /// <summary>Blob path in Storage Account for the logo. Accessed via SAS URL.</summary>
    public string? LogoBlobPath { get; init; }
    public string PrimaryColor { get; init; } = "#0078d4";
    public string AccentColor { get; init; } = "#005a9e";
    public string ApplicationName { get; init; } = "Cloud Imaging";
}
