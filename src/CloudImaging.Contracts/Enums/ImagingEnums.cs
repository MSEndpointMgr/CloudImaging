using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>Machine-readable imaging step identifiers used in API payloads (FR-007).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImagingStepName
{
    FormatDisk,
    DownloadImage,
    ApplyImage,
    ConfigureBoot,
    ApplyRecoveryImage
}

/// <summary>Step execution states.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImagingStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// Fixed, well-known GPT partition types that make up a device's UEFI-bootable disk layout.
/// The admin-configurable <see cref="Models.PartitioningScheme"/> controls only the size and
/// order of these — new/arbitrary partition types are not supported.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PartitionType
{
    /// <summary>EFI System Partition — FAT32, holds the UEFI boot loader.</summary>
    EfiSystem,

    /// <summary>Microsoft Reserved Partition — no filesystem, no drive letter.</summary>
    Msr,

    /// <summary>Main Windows OS partition — NTFS. Its size is always "fill remaining space".</summary>
    Windows,

    /// <summary>Windows Recovery Environment (WinRE) partition — NTFS, hidden via GPT attribute.</summary>
    Recovery
}

/// <summary>
/// Which catalog an asynchronous image publish job will land in once its background verification
/// completes. See <see cref="Models.UploadJob"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UploadJobKind
{
    OsImage,
    BootImage,
    RecoveryImage
}

/// <summary>
/// Lifecycle of an asynchronous image publish job. <see cref="Completed"/> and <see cref="Failed"/>
/// are terminal; the portal stops polling once either is reached.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UploadJobStatus
{
    /// <summary>Accepted and queued. The staged blob passed its file-signature check.</summary>
    Pending,

    /// <summary>Claimed by the background worker: hashing, extracting, and copying.</summary>
    Processing,

    /// <summary>Verified and registered in the catalog. <c>ResultImageId</c> is populated.</summary>
    Completed,

    /// <summary>Rejected or errored. <c>FailureReason</c> explains why, for display in the portal.</summary>
    Failed
}

/// <summary>
/// Which phase of the background publish the worker is currently in. Reported alongside
/// <c>ProgressPercent</c> so the portal can show a real progress bar for work that takes minutes
/// on a multi-GB image, rather than a bar pinned at 100%.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UploadJobStage
{
    /// <summary>Accepted but not yet claimed by the worker.</summary>
    Queued,

    /// <summary>Re-computing the SHA-256 over the whole staged blob.</summary>
    Verifying,

    /// <summary>Streaming <c>sources\install.wim</c> out of an uploaded ISO. OS images only.</summary>
    Extracting,

    /// <summary>Copying the verified blob to its published path and writing the catalog entry.</summary>
    Publishing
}
