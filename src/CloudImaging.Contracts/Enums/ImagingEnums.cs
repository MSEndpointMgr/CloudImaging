namespace CloudImaging.Contracts.Enums;

/// <summary>Machine-readable imaging step identifiers used in API payloads (FR-007).</summary>
public enum ImagingStepName
{
    FormatDisk,
    DownloadImage,
    ApplyImage,
    ConfigureBoot,
    ApplyRecoveryImage
}

/// <summary>Step execution states.</summary>
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
