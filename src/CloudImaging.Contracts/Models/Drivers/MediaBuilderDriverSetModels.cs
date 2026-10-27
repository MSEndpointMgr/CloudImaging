using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models.Drivers;

/// <summary>The current driver set of one model and Windows release, offered for preloading on a USB drive.</summary>
public sealed record MediaBuilderDriverSet
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Vendor.</summary>
    public DriverVendor Vendor { get; init; }

    /// <summary>Vendor name to show.</summary>
    public required string VendorName { get; init; }

    /// <summary>Model display name.</summary>
    public required string ModelName { get; init; }

    /// <summary>Windows release.</summary>
    public WindowsRelease Release { get; init; }

    /// <summary>Release name to show, for example "Windows 11, version 24H2".</summary>
    public required string ReleaseName { get; init; }

    /// <summary>Current set version.</summary>
    public int SetVersion { get; init; }

    /// <summary>Components in the set.</summary>
    public int Components { get; init; }

    /// <summary>Bytes of the set's distinct files.</summary>
    public long Bytes { get; init; }

    /// <summary>Each distinct file, so sizes of several sets count shared files once.</summary>
    public IReadOnlyList<MediaBuilderDriverSetFile> Files { get; init; } = [];
}

/// <summary>A file of a set, by hash and size.</summary>
/// <param name="Sha256">SHA-256, lower-case hex.</param>
/// <param name="SizeBytes">Size in bytes.</param>
public sealed record MediaBuilderDriverSetFile(string Sha256, long SizeBytes);

/// <summary>One model and release to fetch links for.</summary>
public sealed record MediaBuilderDriverSetRef
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Windows release.</summary>
    public WindowsRelease Release { get; init; }
}

/// <summary>Request for read links to the files of chosen driver sets.</summary>
public sealed record MediaBuilderDriverSetLinksRequest
{
    /// <summary>Sets, at most 20.</summary>
    public IReadOnlyList<MediaBuilderDriverSetRef> Sets { get; init; } = [];
}

/// <summary>A chosen set and the version the links are for.</summary>
public sealed record MediaBuilderDriverSetVersion
{
    /// <summary>Model id.</summary>
    public Guid ModelId { get; init; }

    /// <summary>Windows release.</summary>
    public WindowsRelease Release { get; init; }

    /// <summary>Current set version.</summary>
    public int SetVersion { get; init; }
}

/// <summary>A driver file and its read link.</summary>
public sealed record MediaBuilderDriverFile
{
    /// <summary>SHA-256, lower-case hex.</summary>
    public required string Sha256 { get; init; }

    /// <summary>File name.</summary>
    public required string FileName { get; init; }

    /// <summary>Size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Read link, valid for 60 minutes.</summary>
    public required string Url { get; init; }

    /// <summary>When the link expires.</summary>
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Read links for the files of chosen driver sets; a file shared by several sets appears once.</summary>
public sealed record MediaBuilderDriverSetLinks
{
    /// <summary>Sets and their versions.</summary>
    public IReadOnlyList<MediaBuilderDriverSetVersion> Sets { get; init; } = [];

    /// <summary>Distinct files.</summary>
    public IReadOnlyList<MediaBuilderDriverFile> Files { get; init; } = [];
}
