namespace CloudImaging.Client.Services;

/// <summary>Reads the device serial number that signed, token-free Device Gateway requests carry.</summary>
internal static class DeviceSerialNumber
{
    /// <summary>The BIOS serial number, or "UNKNOWN" when WMI has none.</summary>
    public static string Read()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
            using var results = searcher.Get();
            foreach (var item in results)
            {
                using (item)
                {
                    if (item["SerialNumber"]?.ToString() is { Length: > 0 } serial)
                        return serial.Trim();
                }
            }
        }
        catch
        {
            // The gateway only uses the serial for its log; the signature still binds the request.
        }
        return "UNKNOWN";
    }
}
