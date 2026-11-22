using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// HP's BIOS catalog per platform (<c>ftp.hp.com/pub/pcbios/&lt;id&gt;/&lt;id&gt;.xml</c>), the one HP's
/// Client Management Script Library reads. Each <c>Rel</c> entry holds <c>Ver</c>, <c>Date</c>,
/// <c>Bin</c>, <c>RB</c> (roll back allowed), <c>L</c> (1 = critical) and hashes.
/// </summary>
public static partial class HpBiosCatalog
{
    /// <summary>Address of a platform's catalog.</summary>
    public static string CatalogUrl(string platformId) => $"https://ftp.hp.com/pub/pcbios/{platformId}/{platformId}.xml";

    /// <summary>Address of a BIOS file.</summary>
    public static string FileUrl(string platformId, string fileName) => $"https://ftp.hp.com/pub/pcbios/{platformId}/{fileName}";

    /// <summary>
    /// The releases of a catalog, newest version first. Entries without a SHA-256 (old releases
    /// carry none) or without a <c>.bin</c> file are left out, since their download cannot be verified.
    /// </summary>
    public static IReadOnlyList<DriverFirmwareRelease> Parse(Stream content, string platformId)
    {
        ArgumentNullException.ThrowIfNull(content);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(content, settings);
        var releases = new List<DriverFirmwareRelease>();
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "Rel")
            {
                continue;
            }

            var version = reader.GetAttribute("Ver")?.Trim();
            var bin = reader.GetAttribute("Bin")?.Trim();
            var sha256 = reader.GetAttribute("Sha256")?.Trim();
            if (string.IsNullOrEmpty(version) || ParseVersion(version) is null
                || string.IsNullOrEmpty(bin) || !bin.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || bin.IndexOfAny(['/', '\\']) >= 0
                || sha256 is null || !Sha256Pattern().IsMatch(sha256))
            {
                continue;
            }

            releases.Add(new DriverFirmwareRelease
            {
                Version = version,
                ReleasedAt = DateOnly.TryParse(reader.GetAttribute("Date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                FileName = bin,
                Url = FileUrl(platformId, bin),
                Sha256 = sha256.ToLowerInvariant(),
                Critical = reader.GetAttribute("L")?.Trim() == "1",
            });
        }

        return releases
            .GroupBy(r => r.Version, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(r => r.Version, Comparer<string>.Create(CompareVersions))
            .ToList();
    }

    /// <summary>Compares two versions per numeric segment; HP's own tool compares them as text.</summary>
    public static int CompareVersions(string? a, string? b) => BiosVersion.Compare(a, b);

    /// <summary>
    /// The version in a device's SMBIOS BIOS version, for example <c>01.06.01</c> from
    /// <c>X84 Ver. 01.06.01</c>; null when it holds none.
    /// </summary>
    public static string? InstalledVersion(string? smbiosVersion) => BiosVersion.Installed(smbiosVersion);

    private static int[]? ParseVersion(string? version) => BiosVersion.Parse(version);

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Pattern();
}
