using System.IO;
using CloudImaging.Contracts.Models;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// The ADK optional components every boot image gets, in the order Microsoft requires: WMI first,
/// the .NET Framework subset next, then Scripting, which WinPE-PowerShell depends on. PowerShell is
/// what In WinPE setup profile scripts run in.
/// </summary>
public static class WinPeOptionalComponents
{
    /// <summary>Injection order for both bases and both architectures.</summary>
    public static readonly IReadOnlyList<string> InjectionOrder = ["WinPE-WMI", "WinPE-NetFx", "WinPE-Scripting", "WinPE-PowerShell"];

    /// <summary>The components that bring Windows PowerShell, in order.</summary>
    public static readonly IReadOnlyList<string> PowerShell = ["WinPE-Scripting", "WinPE-PowerShell"];

    /// <summary>Progress text while the PowerShell components are added.</summary>
    public const string PowerShellProgressText = "Adding WinPE PowerShell support";

    /// <summary>The ADK folder that holds the optional component cabs for an architecture.</summary>
    public static string Directory(string adkPath, MachineArchitecture architecture) =>
        Path.Combine(adkPath, "Windows Preinstallation Environment", MachineArchitecturePlatform.AdkArchitectureName(architecture), "WinPE_OCs");

    /// <summary>Base cab and en-us language cab of <paramref name="component"/>.</summary>
    public static (string BaseCab, string LanguageCab) PackagePaths(string adkPath, MachineArchitecture architecture, string component)
    {
        var directory = Directory(adkPath, architecture);
        return (Path.Combine(directory, $"{component}.cab"), Path.Combine(directory, "en-us", $"{component}_en-us.cab"));
    }

    /// <summary>Why generation stops when a component's cabs are missing, naming both files.</summary>
    public static string MissingMessage(string directory, string component) =>
        $"Could not find the {component} optional component under \"{directory}\" (expected \"{component}.cab\" and \"en-us\\{component}_en-us.cab\"). " +
        "This ships alongside copype.cmd as part of the Windows ADK WinPE add-on. Reinstall or repair the WinPE add-on (adkwinpesetup.exe) and try again.";
}
