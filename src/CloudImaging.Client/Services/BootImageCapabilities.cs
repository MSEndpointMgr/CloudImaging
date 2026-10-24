using System.IO;

namespace CloudImaging.Client.Services;

/// <summary>
/// What the running boot image can do, read from the image itself so no setting can get out of
/// step with it. Windows RE carries the WLAN service; Media Builder adds the two DLLs it needs from
/// the full OS. The ADK's Windows PE has neither, so the Wi-Fi button is hidden there.
/// </summary>
public static class BootImageCapabilities
{
    private static readonly string[] WifiFiles = ["wlansvc.dll", "dmcmnutils.dll", "mdmregistration.dll"];

    /// <summary>True when the WLAN service and both DLLs it needs are present in System32.</summary>
    /// <param name="fileExists">Test seam; defaults to <see cref="File.Exists(string)"/>.</param>
    /// <param name="system32">Test seam; defaults to the running Windows' System32.</param>
    public static bool SupportsWifi(Func<string, bool>? fileExists = null, string? system32 = null)
    {
        fileExists ??= File.Exists;
        system32 ??= Environment.GetFolderPath(Environment.SpecialFolder.System);
        try
        {
            return WifiFiles.All(file => fileExists(Path.Combine(system32, file)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Relative to System32; Media Builder adds it through WinPE-PowerShell.</summary>
    public const string PowerShellRelativePath = @"WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>True when Windows PowerShell is in the boot image, so In WinPE scripts can run.</summary>
    /// <param name="fileExists">Test seam; defaults to <see cref="File.Exists(string)"/>.</param>
    /// <param name="system32">Test seam; defaults to the running Windows' System32.</param>
    public static bool SupportsPowerShell(Func<string, bool>? fileExists = null, string? system32 = null)
    {
        fileExists ??= File.Exists;
        system32 ??= Environment.GetFolderPath(Environment.SpecialFolder.System);
        try
        {
            return fileExists(Path.Combine(system32, PowerShellRelativePath));
        }
        catch
        {
            return false;
        }
    }
}
