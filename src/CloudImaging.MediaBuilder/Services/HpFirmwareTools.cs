using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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
        await VerifySignaturesAsync(targetFolder, ct);
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

    // Get-AuthenticodeSignature checks the whole chain against the machine's trust store; the
    // signer must be HP, so a validly signed file from anyone else is refused too.
    private static async Task VerifySignaturesAsync(string folder, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => SignedExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (files.Count == 0)
        {
            throw new InvalidOperationException("The HP firmware tools contain no scripts.");
        }

        var listFile = Path.Combine(Path.GetTempPath(), $"ci-hp-tools-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(listFile, files, ct);
        try
        {
            // UTF-8 both ways, so a path with non-ASCII characters matches the list it came from.
            var command = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; Get-Content -LiteralPath $env:CI_FILE_LIST -Encoding UTF8 | ForEach-Object { $s = Get-AuthenticodeSignature -LiteralPath $_; \"$($s.Status)|$($s.SignerCertificate.Subject)|$_\" }";
            var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command));
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.Environment["CI_FILE_LIST"] = listFile;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell could not be started to check HP's signature.");
            string output;
            try
            {
                // Both streams are drained together: a full error pipe would otherwise stall PowerShell.
                var errors = process.StandardError.ReadToEndAsync(ct);
                output = await process.StandardOutput.ReadToEndAsync(ct);
                await errors;
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            var results = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split('|', 3))
                .Where(parts => parts.Length == 3)
                .ToList();
            var bad = files.Where(f => !results.Any(r => string.Equals(r[2], f, StringComparison.OrdinalIgnoreCase) && IsHpSignature(r[0], r[1]))).ToList();
            if (bad.Count > 0)
            {
                throw new InvalidOperationException($"{bad.Count} file(s) of the HP firmware tools are not signed by HP, for example \"{Path.GetFileName(bad[0])}\". Nothing was added to the boot image.");
            }
        }
        finally
        {
            File.Delete(listFile);
        }
    }

    /// <summary>True for a valid signature whose signer is HP Inc.</summary>
    public static bool IsHpSignature(string status, string subject) =>
        status == "Valid" && subject.Contains("O=HP Inc.", StringComparison.OrdinalIgnoreCase);
}
