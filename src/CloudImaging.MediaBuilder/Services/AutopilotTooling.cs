using System.IO;
using CloudImaging.Contracts.Models;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Files a boot image needs so the Cloud Imaging Client can capture a Windows Autopilot hardware
/// hash in WinPE. WinPE has no MDM_DevDetail_Ext01 WMI class, so the hash is produced with the
/// ADK's OA3Tool. OA3Tool reads the TPM through the Platform Crypto Provider (PCPKsp.dll), which
/// base WinPE lacks; the ADK's WinPE-PlatformId optional component ships it, for x64 and ARM64,
/// built from the same release as the WinPE image itself.
/// </summary>
public static class AutopilotTooling
{
    /// <summary>Folder under the Client's install directory (X:\CloudImaging) that holds OA3Tool.</summary>
    public const string ClientRelativeDirectory = @"Tools\Autopilot";
    /// <summary>File name of the ADK's OA3Tool executable.</summary>
    public const string Oa3ToolFileName = "oa3tool.exe";

    /// <summary>TPM driver and TPM Base Services. Requires WinPE-WMI, which every boot image already gets.</summary>
    public const string SecureStartupPackage = "WinPE-SecureStartup";

    /// <summary>Supplies PCPKsp.dll. Requires WinPE-WMI and WinPE-SecureStartup; has no language pack.</summary>
    public const string PlatformIdPackage = "WinPE-PlatformId";

    /// <summary>Path to OA3Tool.exe under the given ADK installation, for the given architecture.</summary>
    public static string Oa3ToolSourcePath(string adkPath, MachineArchitecture architecture) =>
        Path.Combine(adkPath, "Deployment Tools", MachineArchitecturePlatform.AdkArchitectureName(architecture), "Licensing", "OA30", Oa3ToolFileName);

    /// <summary>Paths to the WinPE-SecureStartup base and language cabs, for the given architecture.</summary>
    public static (string BaseCab, string LanguageCab) SecureStartupPackagePaths(string adkPath, MachineArchitecture architecture)
    {
        var ocsDir = OptionalComponentsDirectory(adkPath, architecture);
        return (Path.Combine(ocsDir, $"{SecureStartupPackage}.cab"), Path.Combine(ocsDir, "en-us", $"{SecureStartupPackage}_en-us.cab"));
    }

    /// <summary>Path to the WinPE-PlatformId cab, for the given architecture.</summary>
    public static string PlatformIdPackagePath(string adkPath, MachineArchitecture architecture) =>
        Path.Combine(OptionalComponentsDirectory(adkPath, architecture), $"{PlatformIdPackage}.cab");

    private static string OptionalComponentsDirectory(string adkPath, MachineArchitecture architecture) =>
        Path.Combine(adkPath, "Windows Preinstallation Environment", MachineArchitecturePlatform.AdkArchitectureName(architecture), "WinPE_OCs");

    /// <summary>
    /// Returns why the tooling cannot be included for this build, or null when every source file
    /// is present. <paramref name="fileExists"/> is injectable so the rules are testable without an ADK.
    /// </summary>
    public static string? GetUnavailableReason(string? adkPath, MachineArchitecture targetArchitecture, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;

        if (string.IsNullOrEmpty(adkPath))
        {
            return "The Windows ADK was not found.";
        }

        var oa3Tool = Oa3ToolSourcePath(adkPath, targetArchitecture);
        if (!exists(oa3Tool))
        {
            return $"OA3Tool was not found at \"{oa3Tool}\". Install the Deployment Tools feature of the Windows ADK.";
        }

        var (baseCab, languageCab) = SecureStartupPackagePaths(adkPath, targetArchitecture);
        if (!exists(baseCab) || !exists(languageCab))
        {
            return $"The {SecureStartupPackage} optional component was not found in the Windows ADK WinPE add-on.";
        }

        return exists(PlatformIdPackagePath(adkPath, targetArchitecture))
            ? null
            : $"The {PlatformIdPackage} optional component was not found in the Windows ADK WinPE add-on.";
    }

    /// <summary>Checks the current machine.</summary>
    public static string? GetUnavailableReason(MachineArchitecture targetArchitecture) =>
        GetUnavailableReason(BootImageGenerationService.FindAdkPath(), targetArchitecture);
}
