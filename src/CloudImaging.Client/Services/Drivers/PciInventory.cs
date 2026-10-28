using System.Globalization;
using System.Management;
using CloudImaging.Contracts.Models.Drivers;

namespace CloudImaging.Client.Services.Drivers;

/// <summary>A PCI device of this machine: vendor and device, with the subsystem when it reports one.</summary>
/// <param name="Vendor">Vendor id, four upper-case hex digits.</param>
/// <param name="Device">Device id, four upper-case hex digits.</param>
/// <param name="SubVendor">Subsystem vendor id.</param>
/// <param name="SubDevice">Subsystem device id.</param>
public sealed record PciHardwareId(string Vendor, string Device, string? SubVendor, string? SubDevice);

/// <summary>Reads the PCI devices of this machine.</summary>
public interface IPciInventory
{
    /// <summary>Every PCI device's hardware IDs; blocking WMI, so call it off the UI thread.</summary>
    IReadOnlyList<PciHardwareId> Read();
}

/// <summary>
/// The PCI hardware of the device, from <c>Win32_PnPEntity</c> hardware IDs (research R12), so a Dell
/// component for hardware the device does not have is skipped without being downloaded.
/// </summary>
public sealed class PciInventory : IPciInventory
{
    /// <inheritdoc/>
    public IReadOnlyList<PciHardwareId> Read()
    {
        var devices = new List<PciHardwareId>();
        using var searcher = new ManagementObjectSearcher("SELECT HardwareID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI%'");
        foreach (var entity in searcher.Get().Cast<ManagementObject>())
        {
            using (entity)
            {
                if (entity["HardwareID"] is string[] ids)
                {
                    devices.AddRange(ids.Select(Parse).OfType<PciHardwareId>());
                }
            }
        }

        return devices.Distinct().ToList();
    }

    /// <summary>Reads <c>PCI\VEN_8086&amp;DEV_43F0&amp;SUBSYS_00748086&amp;REV_00</c>; the subsystem is device id then vendor id. Null for anything else.</summary>
    public static PciHardwareId? Parse(string? hardwareId)
    {
        if (hardwareId is null || !hardwareId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? vendor = null;
        string? device = null;
        string? subVendor = null;
        string? subDevice = null;
        foreach (var part in hardwareId[4..].Split('&'))
        {
            if (part.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase) && IsHex(part[4..], 4))
            {
                vendor = part[4..].ToUpperInvariant();
            }
            else if (part.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase) && IsHex(part[4..], 4))
            {
                device = part[4..].ToUpperInvariant();
            }
            else if (part.StartsWith("SUBSYS_", StringComparison.OrdinalIgnoreCase) && IsHex(part[7..], 8))
            {
                subDevice = part[7..11].ToUpperInvariant();
                subVendor = part[11..15].ToUpperInvariant();
            }
        }

        return vendor is null || device is null ? null : new PciHardwareId(vendor, device, subVendor, subDevice);
    }

    /// <summary>
    /// True when a device has hardware a component lists: vendor and device match and, when the
    /// component names a subsystem, the device reports the same subsystem.
    /// </summary>
    public static bool Matches(IReadOnlyList<DriverPciId> component, IReadOnlyList<PciHardwareId> devices)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(devices);
        return component.Any(c => devices.Any(d =>
            string.Equals(c.Vendor, d.Vendor, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Device, d.Device, StringComparison.OrdinalIgnoreCase)
            && (c.SubVendor is null || string.Equals(c.SubVendor, d.SubVendor, StringComparison.OrdinalIgnoreCase))
            && (c.SubDevice is null || string.Equals(c.SubDevice, d.SubDevice, StringComparison.OrdinalIgnoreCase))));
    }

    private static bool IsHex(string value, int length) =>
        value.Length == length && int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
}
