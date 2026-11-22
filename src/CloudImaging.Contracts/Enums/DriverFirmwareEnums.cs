using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Enums;

/// <summary>What a device of a model does with the vendor's BIOS.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverFirmwareAction
{
    /// <summary>The BIOS is staged during imaging and flashes at the restart that ends it.</summary>
    UpdateAtRestart,

    /// <summary>The BIOS is left as it is.</summary>
    Skip,
}

/// <summary>Who gets a newer BIOS a sync finds.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverFirmwareNewVersions
{
    /// <summary>Every device of the model.</summary>
    AvailableImmediately,

    /// <summary>Only devices from the pre-production location, until someone releases it.</summary>
    PreProductionFirst,
}

/// <summary>Where a device draws its power from when it registers.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PowerSource
{
    /// <summary>Not reported, or could not be read.</summary>
    Unknown,

    /// <summary>The AC adapter is connected, or the device has no battery.</summary>
    Ac,

    /// <summary>The device runs on its battery.</summary>
    Battery,
}
