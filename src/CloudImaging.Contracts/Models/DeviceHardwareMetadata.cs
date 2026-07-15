namespace CloudImaging.Contracts.Models;

/// <summary>
/// Hardware metadata collected silently from a WinPE device at session init (FR-001a).
/// All fields are nullable because not every value is guaranteed to be available in WinPE.
/// </summary>
public sealed class DeviceHardwareMetadata
{
    public string? MotherboardManufacturer { get; init; }
    public string? MotherboardModel { get; init; }
    public string? BiosVersion { get; init; }
    public IReadOnlyList<string> NicIdentifiers { get; init; } = [];
    public IReadOnlyList<string> StorageLayout { get; init; } = [];
}
