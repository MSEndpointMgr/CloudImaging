namespace CloudImaging.Contracts.Models;

/// <summary>
/// Hardware metadata collected silently from a WinPE device at session init (FR-001a).
/// All fields are nullable because not every value is guaranteed to be available in WinPE.
/// </summary>
public sealed class DeviceHardwareMetadata
{
    /// <summary>Motherboard manufacturer (e.g. "Dell Inc."), if available in WinPE.</summary>
    public string? MotherboardManufacturer { get; init; }

    /// <summary>Motherboard model (e.g. "Latitude 5540"), if available in WinPE.</summary>
    public string? MotherboardModel { get; init; }

    /// <summary>BIOS/UEFI firmware version string, if available in WinPE.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>MAC addresses of all physical network adapters present at session init.</summary>
    public IReadOnlyList<string> NicIdentifiers { get; init; } = [];

    /// <summary>Summary of the physical disk layout (e.g. disk number and capacity per disk).</summary>
    public IReadOnlyList<string> StorageLayout { get; init; } = [];
}
