using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// HP's Client Management Script Library (CMSL), which stages a BIOS update in Windows PE. HP's
/// licence does not let Cloud Imaging distribute it, so Media Builder downloads it here, on the
/// operator's machine, from the PowerShell Gallery after the operator accepts HP's licence, and
/// copies it into the boot image. It never passes through Cloud Imaging's releases or storage.
/// </summary>
public static class HpFirmwareTools
{
    /// <summary>The tested CMSL version. A newer one is only taken after it has been tried on hardware.</summary>
    public const string Version = "1.9.0";

    /// <summary>HP's licence for <see cref="Version"/>, shown before the operator accepts it.</summary>
    public const string LicenceUrl = "https://www.powershellgallery.com/packages/HPCMSL/1.9.0/DisplayLicense/license.txt";

    /// <summary>The modules the Client imports to stage a BIOS; the rest of the library is not needed in Windows PE.</summary>
    public static readonly IReadOnlyList<string> Modules = ["HP.Private", "HP.Utility", "HP.ClientManagement", "HP.Firmware"];

    /// <summary>Optional components HP's flash path needs on top of PowerShell: disk cmdlets for the EFI partition lookup, and the UEFI mode check.</summary>
    public static readonly IReadOnlyList<string> OptionalComponents = ["WinPE-StorageWMI", "WinPE-SecureBootCmdlets"];

    /// <summary>Where the modules go in the boot image, relative to its root.</summary>
    public static readonly string ImageModulesPath = Path.Combine("Program Files", "WindowsPowerShell", "Modules");

    /// <summary>Progress text while the tools are added to the image.</summary>
    public const string ProgressText = "Adding HP firmware tools";

    private const string GalleryPackageUrl = "https://www.powershellgallery.com/api/v2/package/";
    private const string HpOrganization = "HP Inc.";
    private const string OrganizationOid = "2.5.4.10";
    private static readonly string[] SignedExtensions = [".ps1", ".psm1", ".psd1", ".ps1xml", ".dll"];

    /// <summary>
    /// Downloads the modules into <paramref name="targetFolder"/> as <c>&lt;Module&gt;\&lt;Version&gt;\</c>,
    /// the layout PowerShell loads from, and verifies HP's signature on every script and library.
    /// Each module is taken at exactly the version the pinned HPCMSL release requires, since CMSL's
    /// modules find each other by version.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, string targetFolder, Action<string> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(log);
        Directory.CreateDirectory(targetFolder);
        var library = await DownloadPackageAsync(http, "HPCMSL", Version, ct);
        var versions = RequiredVersions(library);
        foreach (var module in Modules)
        {
            if (!versions.TryGetValue(module, out var version))
            {
                throw new InvalidOperationException($"HP Client Management Script Library {Version} no longer lists {module}. Update Media Builder.");
            }

            log($"Downloading {module} {version} from the PowerShell Gallery.");
            var package = await DownloadPackageAsync(http, module, version, ct);
            Extract(package, Path.Combine(targetFolder, module, version));
        }

        log("Checking HP's signature on the HP firmware tools.");
        await Task.Run(() => VerifySignatures(targetFolder, ct), ct);
        return targetFolder;
    }

    /// <summary>The exact module versions an HPCMSL package requires, from its <c>.nuspec</c>.</summary>
    public static Dictionary<string, string> RequiredVersions(byte[] package)
    {
        ArgumentNullException.ThrowIfNull(package);
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var nuspec = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/'))
            ?? throw new InvalidDataException("The package has no .nuspec.");
        using var stream = nuspec.Open();
        var document = XDocument.Load(stream);
        return document.Descendants().Where(e => e.Name.LocalName == "dependency")
            .Select(e => (Id: (string?)e.Attribute("id"), Version: ((string?)e.Attribute("version"))?.Trim('[', ']', ' ')))
            .Where(d => !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Version) && !d.Version.Contains(','))
            .ToDictionary(d => d.Id!, d => d.Version!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts a module package into <paramref name="folder"/>, leaving out NuGet's own files.
    /// An entry that would land outside the folder stops the extraction.
    /// </summary>
    public static void Extract(byte[] package, string folder)
    {
        ArgumentNullException.ThrowIfNull(package);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var name = Uri.UnescapeDataString(entry.FullName);
            if (name.Length == 0 || name.EndsWith('/') || name.StartsWith("_rels/", StringComparison.Ordinal) || name.StartsWith("package/", StringComparison.Ordinal)
                || name == "[Content_Types].xml" || (name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !name.Contains('/')))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(root, name));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The package entry \"{name}\" points outside its folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static async Task<byte[]> DownloadPackageAsync(HttpClient http, string id, string version, CancellationToken ct)
    {
        try
        {
            return await http.GetByteArrayAsync(new Uri($"{GalleryPackageUrl}{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}"), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not download {id} {version} from the PowerShell Gallery: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // A time-out, not the operator's Cancel.
            throw new InvalidOperationException($"Could not download {id} {version} from the PowerShell Gallery: it took too long.", ex);
        }
    }

    // Each file's own signature, never a Windows catalog: Get-AuthenticodeSignature reports Microsoft for
    // libraries HP ships byte-identical to ones in Windows (System.Memory.dll), although HP signed them.
    internal static void VerifySignatures(string folder, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => SignedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("The HP firmware tools contain no scripts.");
        }

        var bad = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(SignerOrganization(file), HpOrganization, StringComparison.Ordinal))
            {
                bad.Add(file);
            }
        }

        if (bad.Count > 0)
        {
            throw new InvalidOperationException($"{bad.Count} file(s) of the HP firmware tools are not signed by HP, for example \"{Path.GetFileName(bad[0])}\". Nothing was added to the boot image.");
        }
    }

    /// <summary>
    /// The organization (O) of whoever signed the file itself, after Windows has checked the signature
    /// and its chain, or null when the file has no signature of its own that Windows trusts.
    /// </summary>
    internal static string? SignerOrganization(string path)
    {
        var action = GenericVerifyV2;
        var pathPointer = Marshal.StringToHGlobalUni(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        var data = new WintrustData { Size = (uint)Marshal.SizeOf<WintrustData>(), UiChoice = WtdUiNone, UnionChoice = WtdChoiceFile, File = filePointer, StateAction = WtdStateActionVerify };
        try
        {
            Marshal.StructureToPtr(new WintrustFileInfo { Size = (uint)Marshal.SizeOf<WintrustFileInfo>(), FilePath = pathPointer }, filePointer, fDeleteOld: false);
            if (WinVerifyTrust(NoInteractiveUser, ref action, ref data) != 0)
            {
                return null;
            }

            var provider = WTHelperProvDataFromStateData(data.StateData);
            var signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, counterSigner: false, 0);
            var providerCertificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (providerCertificate == IntPtr.Zero)
            {
                return null;
            }

            // CRYPT_PROVIDER_CERT starts with a DWORD size, then the pointer-aligned certificate context.
            using var certificate = new X509Certificate2(Marshal.ReadIntPtr(providerCertificate, IntPtr.Size));
            return certificate.SubjectName.EnumerateRelativeDistinguishedNames()
                .Where(n => !n.HasMultipleElements && n.GetSingleElementType().Value == OrganizationOid)
                .Select(n => n.GetSingleElementValue())
                .FirstOrDefault();
        }
        finally
        {
            if (data.StateData != IntPtr.Zero)
            {
                data.StateAction = WtdStateActionClose;
                _ = WinVerifyTrust(NoInteractiveUser, ref action, ref data);
            }

            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    private const uint WtdUiNone = 2;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private static readonly IntPtr NoInteractiveUser = new(-1);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WintrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr providerData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificateIndex);
}
