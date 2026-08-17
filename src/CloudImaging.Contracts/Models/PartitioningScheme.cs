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
/// device imaging session — there is a single global scheme, not a per-image or per-model one.
/// The scheme in effect at session-creation time is snapshotted onto the <see cref="DeviceSession"/>
/// so that later admin edits do not affect sessions already in progress.
/// </summary>
public sealed class PartitioningScheme
{
    public required IReadOnlyList<PartitionDefinition> Partitions { get; init; }

    public DateTimeOffset LastModifiedAt { get; init; }

    /// <summary>The standard UEFI-bootable layout: ESP (100MB) → MSR (16MB) → Windows (fill) → Recovery (990MB).</summary>
    public static PartitioningScheme Default { get; } = new()
    {
        Partitions =
        [
            new PartitionDefinition { PartitionType = PartitionType.EfiSystem, SizeMb = 100, Order = 0 },
            new PartitionDefinition { PartitionType = PartitionType.Msr, SizeMb = 16, Order = 1 },
            new PartitionDefinition { PartitionType = PartitionType.Windows, SizeMb = 0, Order = 2 },
            new PartitionDefinition { PartitionType = PartitionType.Recovery, SizeMb = 990, Order = 3 },
        ],
    };
}
