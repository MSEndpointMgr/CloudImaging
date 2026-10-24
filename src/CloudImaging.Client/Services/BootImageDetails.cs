using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using CloudImaging.Contracts.Models;

namespace CloudImaging.Client.Services;

/// <summary>Where the running boot image was started from.</summary>
public enum BootMediaKind
{
    /// <summary>No preparation manifest was found, e.g. a VM booted from the WIM or PXE.</summary>
    Other,

    /// <summary>A USB drive prepared by Media Builder.</summary>
    UsbDrive,

    /// <summary>An ISO built by Media Builder.</summary>
    Iso,
}

/// <summary>One line of the title bar's info panel.</summary>
/// <param name="Label">What the value is.</param>
/// <param name="Value">The value shown.</param>
public sealed record BootImageDetailRow(string Label, string Value);

/// <summary>
/// What the title bar's info button shows about the running Client and boot image. Read once at
/// launch: a boot media update later rewrites the media manifest, which would then describe the
/// next boot instead of this one.
/// </summary>
/// <param name="ClientVersion">Client release, without build metadata.</param>
/// <param name="BootImageVersion">Version the portal lists, from the media manifest.</param>
/// <param name="BuiltAt">When Media Builder built the running image, from the manifest inside it.</param>
/// <param name="Base">Windows PE or Windows RE.</param>
/// <param name="Architecture">Processor architecture of the running Windows.</param>
/// <param name="Media">Where the image was started from.</param>
/// <param name="LocationName">Location chosen when the media was prepared.</param>
public sealed record BootImageDetails(
    string? ClientVersion,
    string? BootImageVersion,
    DateTimeOffset? BuiltAt,
    BootImageBase Base,
    MachineArchitecture Architecture,
    BootMediaKind Media,
    string? LocationName)
{
    /// <summary>Written by Media Builder to the root of the boot image.</summary>
    internal const string EmbeddedManifestFileName = "ci-manifest.json";

    /// <summary>The rows the info panel shows, in order.</summary>
    public IReadOnlyList<BootImageDetailRow> Rows =>
    [
        new("Client", ClientVersion ?? "Unknown"),
        new("Boot image", BootImageVersion ?? "Not recorded"),
        new("Built", BuiltAt is { } built ? built.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "Unknown"),
        new("Type", $"{(Base == BootImageBase.WindowsRE ? "Windows RE" : "Windows PE")}, {(Architecture == MachineArchitecture.Arm64 ? "ARM64" : "x64")}"),
        new("Started from", Media switch { BootMediaKind.UsbDrive => "USB drive", BootMediaKind.Iso => "ISO image", _ => "Other media" }),
        new("Location", string.IsNullOrWhiteSpace(LocationName) ? "None" : LocationName),
    ];

    /// <summary>Reads the running image and its media. Never throws.</summary>
    public static BootImageDetails Read()
    {
        var architecture = RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? MachineArchitecture.Arm64 : MachineArchitecture.X64;
        try
        {
            var mediaPath = FindMediaManifestPath(out var media);
            var embeddedPath = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"X:\", EmbeddedManifestFileName);
            return From(ClientReleaseVersion(), TryRead<UsbPreparationManifest>(mediaPath), media, TryRead<BootImageManifest>(embeddedPath), BootImageCapabilities.SupportsWifi(), architecture);
        }
        catch
        {
            // WMI can be missing or broken in a custom image; the Client version is still worth showing.
            return From(ClientReleaseVersion(), null, BootMediaKind.Other, null, BootImageCapabilities.SupportsWifi(), architecture);
        }
    }

    internal static BootImageDetails From(string? clientVersion, UsbPreparationManifest? mediaManifest, BootMediaKind media, BootImageManifest? embedded, bool supportsWifi, MachineArchitecture architecture) =>
        new(
            clientVersion,
            mediaManifest?.BootImageVersion,
            embedded?.CreatedAt,
            embedded?.Base ?? mediaManifest?.BootImageBase ?? (supportsWifi ? BootImageBase.WindowsRE : BootImageBase.WindowsPE),
            architecture,
            mediaManifest is null ? BootMediaKind.Other : media,
            mediaManifest?.LocationName);

    /// <summary>Client release number; the "+commit" build metadata is dropped.</summary>
    public static string? ClientReleaseVersion() =>
        typeof(BootImageDetails).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];

    /// <summary>The preparation manifest on the USB BOOT volume, or else on the root of a CD/DVD drive for media booted from an ISO.</summary>
    internal static string? FindMediaManifestPath(out BootMediaKind media)
    {
        var bootDrive = BootImageSelfUpdateService.FindBootVolumeDriveLetter();
        if (bootDrive is not null)
        {
            var usbManifest = Path.Combine(bootDrive.TrimEnd('\\') + "\\", UsbPreparationManifest.FileName);
            if (File.Exists(usbManifest))
            {
                media = BootMediaKind.UsbDrive;
                return usbManifest;
            }
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.CDRom || !drive.IsReady) continue;
                var isoManifest = Path.Combine(drive.RootDirectory.FullName, UsbPreparationManifest.FileName);
                if (File.Exists(isoManifest))
                {
                    media = BootMediaKind.Iso;
                    return isoManifest;
                }
            }
        }
        catch (IOException)
        {
            // An unreadable drive just means no ISO manifest.
        }

        media = BootMediaKind.Other;
        return null;
    }

    private static T? TryRead<T>(string? path) where T : class
    {
        try
        {
            return path is not null && File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }
}
