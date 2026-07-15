using CloudImaging.Contracts.Enums;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Registration payload submitted by the Cloud Imaging Client when initiating a session (FR-001a, FR-010).
/// </summary>
public sealed class DeviceRegistrationPayload
{
    public required string SerialNumber { get; init; }
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }

    /// <summary>MAC address — informational only.</summary>
    public string? MacAddress { get; init; }

    /// <summary>Detailed hardware metadata collected silently for audit purposes (FR-001a).</summary>
    public DeviceHardwareMetadata? Hardware { get; init; }
}
