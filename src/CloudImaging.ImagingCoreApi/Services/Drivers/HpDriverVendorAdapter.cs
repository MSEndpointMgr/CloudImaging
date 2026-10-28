using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// HP: models from the platform list (system ID, product names, releases per architecture) and
/// components from the per-platform reference file (contracts/vendor-catalogs.md section 1,
/// research R24). Both are read with <see cref="XmlReader"/>, one element at a time.
/// </summary>
public sealed class HpDriverVendorAdapter : IDriverVendorAdapter
{
    /// <summary>HP's platform list.</summary>
    public const string PlatformListUrl = "https://hpia.hpcloud.hp.com/ref/platformList.cab";

    private const string ArmArchitecture = "ARM64";

    /// <inheritdoc/>
    public DriverVendor Vendor => DriverVendor.Hp;

    /// <inheritdoc/>
    public string MatchKeyType => "system ID";

    /// <inheritdoc/>
    public string ModelListUrl => PlatformListUrl;

    /// <inheritdoc/>
    public string? DeviceMatchKey(DeviceHardwareMetadata? hardware) => hardware?.MotherboardModel;

    /// <inheritdoc/>
    public bool IsVendorOf(string? manufacturer) =>
        manufacturer is not null && (manufacturer.Contains("HP", StringComparison.OrdinalIgnoreCase) || manufacturer.Contains("Hewlett", StringComparison.OrdinalIgnoreCase));

    /// <summary>Catalog model key: upper-case system ID and architecture (<c>8C26_X64</c>).</summary>
    public static string ModelKey(string systemId, MachineArchitecture architecture) =>
        $"{systemId.ToUpperInvariant()}_{(architecture == MachineArchitecture.Arm64 ? "ARM64" : "X64")}";

    /// <inheritdoc/>
    public IReadOnlyList<DriverVendorModel> ParseModelList(Stream content, DateTimeOffset seenAt)
    {
        var models = new Dictionary<string, DriverVendorModel>(StringComparer.Ordinal);
        foreach (var platform in ReadElements(content, "Platform", depth: 1))
        {
            var systemId = platform.Element("SystemID")?.Value.Trim();
            var name = platform.Elements("ProductName").Select(p => p.Value.Trim()).FirstOrDefault(n => n.Length > 0);
            if (string.IsNullOrEmpty(systemId) || name is null)
            {
                continue;
            }

            foreach (var group in platform.Elements("OS").GroupBy(os => ParseArchitecture(os.Element("OSArchitecture")?.Value)))
            {
                var releases = group
                    .Select(os => ParseRelease(os.Element("OSDescription")?.Value, os.Element("OSReleaseIdFilename")?.Value))
                    .OfType<WindowsRelease>()
                    .Distinct()
                    .Order()
                    .ToList();
                if (releases.Count == 0)
                {
                    continue;
                }

                var key = ModelKey(systemId, group.Key);
                models[key] = new DriverVendorModel(key, new DriverCatalogModelRecord
                {
                    Name = name,
                    MatchKeys = [systemId.ToUpperInvariant()],
                    Architecture = group.Key,
                    Releases = releases,
                    SeenAt = seenAt,
                });
            }
        }

        return [.. models.Values.OrderBy(m => m.Key, StringComparer.Ordinal)];
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ComponentCatalogUrls(DriverCatalogModelRecord model, WindowsRelease release)
    {
        ArgumentNullException.ThrowIfNull(model);
        var id = model.MatchKeys[0].ToLowerInvariant();
        var architecture = model.Architecture == MachineArchitecture.Arm64 ? "arm64" : "64";
        var (os, releaseName) = ReleaseParts(release);
        return [$"https://hpia.hpcloud.hp.com/ref/{id}/{id}_{architecture}_{os}.{releaseName.ToLowerInvariant()}.cab"];
    }

    /// <inheritdoc/>
    public IReadOnlyList<DriverVendorComponent> ParseComponents(Stream content, DriverCatalogModelRecord model, WindowsRelease release, DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(model);
        var current = new List<XElement>();
        var supersedes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (section, entry) in ReadUpdateInfos(content))
        {
            var id = entry.Element("Id")?.Value.Trim();
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            if ((string?)entry.Attribute("Supersedes") is { Length: > 0 } older)
            {
                supersedes[id] = older.Trim();
            }

            if (section == "Solutions")
            {
                current.Add(entry);
            }
        }

        var arm = model.Architecture == MachineArchitecture.Arm64;
        var components = new List<DriverVendorComponent>();
        foreach (var entry in current)
        {
            var name = Text(entry, "Name") ?? string.Empty;
            var category = Text(entry, "Category") ?? string.Empty;
            if (IsExcluded(name, category))
            {
                continue;
            }

            var id = Text(entry, "Id")!;
            var silentInstall = Text(entry, "SilentInstall");
            var dpb = IsTrue(Text(entry, "DPBCompliant"));
            var ssm = IsTrue(Text(entry, "SSMCompliant"));
            var installable = IsInstallable(ssm, silentInstall);
            var hasApp = (Text(entry, "ContentTypes") ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Contains("Uwp", StringComparer.OrdinalIgnoreCase);
            var (defaultAction, allowed) = Classify(dpb, hasApp, installable, category, arm);
            var fileName = $"{id}.exe";
            var size = long.TryParse(Text(entry, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize) ? parsedSize : 0;
            DateTimeOffset? released = DateTimeOffset.TryParse(Text(entry, "DateReleased"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

            components.Add(new DriverVendorComponent(LineageKey(id, supersedes), new DriverComponentRecord
            {
                ComponentId = id,
                Name = name,
                Category = category,
                Version = Text(entry, "Version"),
                ReleasedAt = released,
                Size = size,
                Sha256 = Text(entry, "SHA256")?.ToLowerInvariant(),
                Url = WithScheme(Text(entry, "Url")),
                FileName = fileName,
                AllowedActions = allowed,
                DefaultAction = defaultAction,
                Extract = arm ? DriverExtractKind.None : DriverExtractKind.Hp,
                InstallCommand = InstallCommand(arm, installable, fileName, silentInstall),
                Details =
                [
                    new DriverDetailField { Label = "Offline injection", Value = dpb && !arm ? "Yes" : "No" },
                    new DriverDetailField { Label = "Contains an app", Value = hasApp ? "Yes" : "No" },
                    new DriverDetailField { Label = "Silent install", Value = installable ? "Supported" : "Not supported" },
                    new DriverDetailField { Label = "Release type", Value = Text(entry, "ReleaseType") ?? "Not specified" },
                ],
                DescriptionUrl = WithScheme(Text(entry, "ReleaseNotesUrl")),
                Description = Truncate(Text(entry, "Description"), 2000),
                AddedAt = seenAt,
                UpdatedAt = seenAt,
            }));
        }

        return components;
    }

    /// <summary>Firmware, BIOS and dock firmware are never stored (FR-003).</summary>
    public static bool IsExcluded(string name, string category) =>
        category.StartsWith("BIOS", StringComparison.OrdinalIgnoreCase)
        || category.Equals("Firmware", StringComparison.OrdinalIgnoreCase)
        || category.StartsWith("Dock - Firmware", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Firmware", StringComparison.OrdinalIgnoreCase);

    /// <summary>FR-015: SSM compliant and a silent install command that is not a placeholder.</summary>
    public static bool IsInstallable(bool ssmCompliant, string? silentInstall) =>
        ssmCompliant
        && !string.IsNullOrWhiteSpace(silentInstall)
        && !silentInstall.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)
        && !silentInstall.Trim().Equals("NA", StringComparison.OrdinalIgnoreCase)
        && !silentInstall.Contains("not support", StringComparison.OrdinalIgnoreCase);

    /// <summary>The HP classification table of FR-016, rows evaluated top to bottom, then the ARM64 rule.</summary>
    public static (DriverAction Default, IReadOnlyList<DriverAction> Allowed) Classify(bool dpbCompliant, bool hasApp, bool installable, string category, bool arm64)
    {
        ArgumentNullException.ThrowIfNull(category);
        (DriverAction, DriverAction[]) row;
        if (dpbCompliant && !hasApp)
        {
            row = (DriverAction.Offline, [DriverAction.Offline, DriverAction.Skip]);
        }
        else if (dpbCompliant && installable)
        {
            row = (DriverAction.OfflineAndSpecialize, [DriverAction.OfflineAndSpecialize, DriverAction.Offline, DriverAction.Skip]);
        }
        else if (dpbCompliant)
        {
            row = (DriverAction.Offline, [DriverAction.Offline, DriverAction.Skip]);
        }
        else if (installable && category.StartsWith("Software - ", StringComparison.OrdinalIgnoreCase))
        {
            row = (DriverAction.Skip, [DriverAction.Skip, DriverAction.Specialize]);
        }
        else if (installable && category.StartsWith("Driver - ", StringComparison.OrdinalIgnoreCase))
        {
            row = (DriverAction.Specialize, [DriverAction.Specialize, DriverAction.Skip]);
        }
        else
        {
            row = (DriverAction.Skip, [DriverAction.Skip]);
        }

        if (!arm64)
        {
            return row;
        }

        // HP's packages are x64 programs the ARM64 boot environment cannot run: nothing offline,
        // and what would have been injected runs during specialize instead.
        var allowed = row.Item2.Where(a => a is DriverAction.Specialize or DriverAction.Skip).ToList();
        if (installable && row.Item2.Any(a => a is DriverAction.Offline or DriverAction.OfflineAndSpecialize) && !allowed.Contains(DriverAction.Specialize))
        {
            allowed.Insert(0, DriverAction.Specialize);
        }

        if (!installable)
        {
            return (DriverAction.Skip, [DriverAction.Skip]);
        }

        var defaultAction = row.Item1 is DriverAction.Offline or DriverAction.OfflineAndSpecialize ? DriverAction.Specialize : row.Item1;
        return (defaultAction, allowed.Count == 0 ? [DriverAction.Skip] : allowed);
    }

    /// <summary>Lineage key: the oldest ID reachable through <c>Supersedes</c>.</summary>
    public static string LineageKey(string id, IReadOnlyDictionary<string, string> supersedes)
    {
        ArgumentNullException.ThrowIfNull(supersedes);
        var key = id.ToLowerInvariant();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        while (supersedes.TryGetValue(key, out var older) && seen.Add(older))
        {
            key = older.ToLowerInvariant();
        }

        return key;
    }

    private static (string Os, string Release) ReleaseParts(WindowsRelease release) => release switch
    {
        WindowsRelease.Windows10Version22H2 => ("10.0", "22H2"),
        WindowsRelease.Windows11Version23H2 => ("11.0", "23H2"),
        WindowsRelease.Windows11Version24H2 => ("11.0", "24H2"),
        WindowsRelease.Windows11Version25H2 => ("11.0", "25H2"),
        _ => throw new ArgumentOutOfRangeException(nameof(release), release, "Unknown Windows release."),
    };

    private static WindowsRelease? ParseRelease(string? description, string? releaseId)
    {
        var windows11 = description?.Contains("Windows 11", StringComparison.OrdinalIgnoreCase) == true;
        var windows10 = description?.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) == true;
        return (windows10, windows11, releaseId?.Trim().ToUpperInvariant()) switch
        {
            (true, false, "22H2") => WindowsRelease.Windows10Version22H2,
            (false, true, "23H2") => WindowsRelease.Windows11Version23H2,
            (false, true, "24H2") => WindowsRelease.Windows11Version24H2,
            (false, true, "25H2") => WindowsRelease.Windows11Version25H2,
            _ => null,
        };
    }

    private static MachineArchitecture ParseArchitecture(string? value) =>
        string.Equals(value?.Trim(), ArmArchitecture, StringComparison.OrdinalIgnoreCase) ? MachineArchitecture.Arm64 : MachineArchitecture.X64;

    private static DriverInstallCommand? InstallCommand(bool arm64, bool installable, string fileName, string? silentInstall)
    {
        if (!installable)
        {
            return null;
        }

        if (arm64)
        {
            return new DriverInstallCommand { Executable = fileName, Arguments = "/s" };
        }

        var text = silentInstall!.Trim();
        string executable;
        string rest;
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            executable = close > 0 ? text[1..close] : text.Trim('"');
            rest = close > 0 ? text[(close + 1)..] : string.Empty;
        }
        else
        {
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            executable = space > 0 ? text[..space] : text;
            rest = space > 0 ? text[space..] : string.Empty;
        }

        rest = rest.Trim();
        return new DriverInstallCommand { Executable = executable, Arguments = rest.Length == 0 ? null : rest };
    }

    private static string? WithScheme(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        return url.Contains("://", StringComparison.Ordinal) ? url : $"https://{url}";
    }

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max];

    private static string? Text(XElement element, string name) => element.Element(name)?.Value.Trim();

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<XElement> ReadElements(Stream content, string name, int depth)
    {
        using var reader = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == depth && reader.LocalName == name)
            {
                yield return (XElement)XNode.ReadFrom(reader);
                continue;
            }

            reader.Read();
        }
    }

    private static IEnumerable<(string Section, XElement Entry)> ReadUpdateInfos(Stream content)
    {
        using var reader = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });
        reader.MoveToContent();
        var section = string.Empty;
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Depth == 1)
                {
                    section = reader.LocalName;
                    if (section is not ("Solutions" or "Solutions-Superseded"))
                    {
                        reader.Skip();
                        continue;
                    }
                }
                else if (reader.Depth == 2 && reader.LocalName == "UpdateInfo")
                {
                    yield return (section, (XElement)XNode.ReadFrom(reader));
                    continue;
                }
            }

            reader.Read();
        }
    }
}
