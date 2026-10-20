using System.Text.Json.Serialization;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// The Windows image a boot image is built on. Windows RE carries the WLAN stack, so only those
/// boot images can join Wi-Fi; Windows PE from the ADK is wired-only.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BootImageBase
{
    /// <summary>Windows PE from the Windows ADK. Ethernet only.</summary>
    WindowsPE,

    /// <summary>Windows RE taken from a Windows installation source. Adds Wi-Fi.</summary>
    WindowsRE,
}

/// <summary>What a WIM's XML metadata says about one of its images.</summary>
/// <param name="ProcessorArchitecture">Raw PROCESSOR_ARCHITECTURE (9 = x64, 12 = arm64).</param>
/// <param name="Build">Windows build number, e.g. 26100.</param>
/// <param name="ServicePackBuild">Update revision, e.g. 4061.</param>
/// <param name="DefaultLanguage">Default language tag, e.g. en-US.</param>
public sealed record WimImageInfo(int? ProcessorArchitecture, int? Build, int? ServicePackBuild, string? DefaultLanguage);
