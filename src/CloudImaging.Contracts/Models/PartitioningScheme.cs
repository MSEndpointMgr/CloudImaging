using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// A single partition's configuration within a <see cref="PartitioningScheme"/>.
/// </summary>
public sealed class PartitionDefinition
{
    public required PartitionType PartitionType { get; init; }

    /// <summary>
    /// Requested partition size in megabytes. Ignored for <see cref="Enums.PartitionType.Windows"/>,
    /// which always fills the remaining space on the disk regardless of this value.
    /// </summary>
    public int SizeMb { get; init; }

    /// <summary>Position of this partition on the disk, ascending (0 = created first).</summary>
    public int Order { get; init; }
}

/// <summary>
/// Deployment-wide, admin-configurable disk partitioning scheme (Key Entities). Applies to every
/// device imaging session. There is a single global scheme, not a per-image or per-model one.
/// The scheme in effect at session-creation time is snapshotted onto the <see cref="DeviceSession"/>
/// so that later admin edits do not affect sessions already in progress.
/// </summary>
public sealed class PartitioningScheme
{
    public required IReadOnlyList<PartitionDefinition> Partitions { get; init; }

    public DateTimeOffset LastModifiedAt { get; init; }

    /// <summary>
    /// The standard UEFI-bootable layout: ESP (500MB), MSR (16MB), Windows (fill), Recovery (990MB).
    /// <para>
    /// ESP is 500MB rather than the 100MB Windows Setup creates interactively. Microsoft's current
    /// OEM guidance puts the floor at 200MB (300MB on 4K-native drives), and their own
    /// CreatePartitions-UEFI.txt sample uses <c>create partition efi size=200</c>. 500MB is what MDT
    /// and Configuration Manager task sequences have long defaulted to, and what most enterprises
    /// and OEMs ship, because the ESP also absorbs OEM firmware capsule updates and any extra boot
    /// loaders over the life of the device. An undersized ESP is painful to grow after the fact.
    /// </para>
    /// </summary>
    public static PartitioningScheme Default { get; } = new()
    {
        Partitions =
        [
            new PartitionDefinition { PartitionType = PartitionType.EfiSystem, SizeMb = 500, Order = 0 },
            new PartitionDefinition { PartitionType = PartitionType.Msr, SizeMb = 16, Order = 1 },
            new PartitionDefinition { PartitionType = PartitionType.Windows, SizeMb = 0, Order = 2 },
            new PartitionDefinition { PartitionType = PartitionType.Recovery, SizeMb = 990, Order = 3 },
        ],
    };
}
