namespace CloudImaging.Contracts.Models;

/// <summary>
/// Hardware metadata collected silently from a WinPE device at session init (FR-001a).
/// All fields are nullable because not every value is guaranteed to be available in WinPE.
/// </summary>
public sealed class DeviceHardwareMetadata
{
    /// <summary>Motherboard manufacturer, as reported by firmware.</summary>
    public string? MotherboardManufacturer { get; init; }

    /// <summary>Motherboard model, as reported by firmware.</summary>
    public string? MotherboardModel { get; init; }

    /// <summary>BIOS/firmware version string.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>MAC addresses of every network adapter detected.</summary>
    public IReadOnlyList<string> NicIdentifiers { get; init; } = [];

    /// <summary>Storage device descriptors detected on the machine.</summary>
    public IReadOnlyList<string> StorageLayout { get; init; } = [];
}
