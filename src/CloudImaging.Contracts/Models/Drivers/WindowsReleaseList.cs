using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>The Windows releases the portal offers for OS images and models (GET drivers/windows-releases).</summary>
public sealed record WindowsReleaseList
{
    /// <summary>Newest first: Microsoft's feature updates and the releases Cloud Imaging ships with.</summary>
    public IReadOnlyList<WindowsRelease> Releases { get; init; } = [];

    /// <summary>When Microsoft's list was last read; null when it never was.</summary>
    public DateTimeOffset? RefreshedAt { get; init; }

    /// <summary>Why Microsoft's list could not be read the last time it was tried; null when it was.</summary>
    public string? Problem { get; init; }
}
