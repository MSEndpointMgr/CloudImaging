using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// Dell: models from the catalog index (one catalog model per model name with every SKU and its
/// per-SKU catalog) and components from the per-SKU catalogs (contracts/vendor-catalogs.md
/// section 2). Both are UTF-16 XML in the <c>openmanage/cm/dm</c> namespace, read one element at
/// a time with <see cref="XmlReader"/>.
/// </summary>
public sealed class DellDriverVendorAdapter : IDriverVendorAdapter
{
    /// <summary>Dell's client catalog index.</summary>
    public const string CatalogIndexUrl = "https://downloads.dell.com/catalog/CatalogIndexPC.cab";

    private const string DownloadBase = "https://downloads.dell.com/";
    private const string ArmCollector = "ARM64";

    /// <summary>Dell has no release granularity: a model is offered for every Windows 11 release.</summary>
    private static readonly WindowsRelease[] Windows11Releases = [WindowsRelease.Windows11Version23H2, WindowsRelease.Windows11Version24H2, WindowsRelease.Windows11Version25H2];

    /// <inheritdoc/>
    public DriverVendor Vendor => DriverVendor.Dell;

    /// <inheritdoc/>
    public string MatchKeyType => "system SKU";

    /// <inheritdoc/>
    public string ModelListUrl => CatalogIndexUrl;

    /// <inheritdoc/>
    public string? DeviceMatchKey(DeviceHardwareMetadata? hardware) => hardware?.SystemSku;

    /// <inheritdoc/>
    public bool IsVendorOf(string? manufacturer) => manufacturer?.Contains("Dell", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Catalog model key: the model name in upper case with spaces as underscores (<c>LATITUDE_5450</c>).</summary>
    public static string ModelKey(string name) => string.Join('_', name.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <inheritdoc/>
    public IReadOnlyList<DriverVendorModel> ParseModelList(Stream content, DateTimeOffset seenAt)
    {
        var skus = new List<(string Name, DriverSkuCatalog Catalog, bool Arm)>();
        foreach (var group in ReadElements(content, "GroupManifest"))
        {
            var manifest = Child(group, "ManifestInformation");
            var path = (string?)manifest?.Attribute("path");
            if (manifest is null || string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var sha = Sha256(manifest);
            var arm = string.Equals((string?)manifest.Attribute("inventoryCollectorType"), ArmCollector, StringComparison.OrdinalIgnoreCase);
            foreach (var model in group.Descendants().Where(e => e.Name.LocalName == "Model"))
            {
                var sku = ((string?)model.Attribute("systemID"))?.Trim();
                var name = Display(model);
                if (!string.IsNullOrEmpty(sku) && !string.IsNullOrEmpty(name))
                {
                    skus.Add((name, new DriverSkuCatalog(DriverTables.NormalizeKey(sku), path.Trim(), sha), arm));
                }
            }
        }

        return [.. skus
            .GroupBy(s => ModelKey(s.Name), StringComparer.Ordinal)
            .Select(g => new DriverVendorModel(g.Key, new DriverCatalogModelRecord
            {
                Name = g.First().Name,
                MatchKeys = g.Select(s => s.Catalog.Sku).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                Architecture = g.All(s => s.Arm) ? MachineArchitecture.Arm64 : MachineArchitecture.X64,
                Releases = Windows11Releases,
                SkuCatalogs = g.Select(s => s.Catalog).DistinctBy(c => c.Sku, StringComparer.Ordinal).OrderBy(c => c.Sku, StringComparer.Ordinal).ToList(),
                SeenAt = seenAt,
            }))
            .OrderBy(m => m.Key, StringComparer.Ordinal)];
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ComponentCatalogUrls(DriverCatalogModelRecord model, WindowsRelease release)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.SkuCatalogs.Select(c => DownloadBase + c.Path.TrimStart('/')).ToList();
    }

    /// <inheritdoc/>
    public string? CatalogSha256(DriverCatalogModelRecord model, string url)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.SkuCatalogs.FirstOrDefault(c => string.Equals(DownloadBase + c.Path.TrimStart('/'), url, StringComparison.OrdinalIgnoreCase))?.Sha256;
    }

    /// <inheritdoc/>
    public IReadOnlyList<DriverVendorComponent> ParseComponents(Stream content, DriverCatalogModelRecord model, WindowsRelease release, DateTimeOffset seenAt)
    {
        ArgumentNullException.ThrowIfNull(model);
        var windows = release == WindowsRelease.Windows10Version22H2 ? "Windows 10" : "Windows 11";
        var architecture = model.Architecture == MachineArchitecture.Arm64 ? "ARM64" : "x64";
        var newest = new Dictionary<string, DriverVendorComponent>(StringComparer.Ordinal);
        foreach (var entry in ReadElements(content, "SoftwareComponent"))
        {
            var name = Display(Child(entry, "Name")) ?? string.Empty;
            var type = (string?)Child(entry, "ComponentType")?.Attribute("value") ?? string.Empty;
            if (IsExcluded(name, type) || !SupportsWindows(entry, windows, architecture))
            {
                continue;
            }

            var packageType = (string?)entry.Attribute("packageType") ?? string.Empty;
            var path = ((string?)entry.Attribute("path"))?.Trim();
            var packageId = ((string?)entry.Attribute("packageID"))?.Trim();
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(packageId))
            {
                continue;
            }

            var devices = Descendants(entry, "SupportedDCHDevices").Concat(Descendants(entry, "SupportedDevices")).SelectMany(e => e.Elements().Where(d => d.Name.LocalName == "Device")).ToList();
            var bundlesApp = name.Contains("Driver and Application", StringComparison.OrdinalIgnoreCase)
                || devices.Any(d => string.Equals((string?)d.Attribute("infType"), "app", StringComparison.OrdinalIgnoreCase));
            var (defaultAction, allowed) = Classify(type, packageType, bundlesApp);
            var specialize = allowed.Any(a => a is DriverAction.Specialize or DriverAction.OfflineAndSpecialize);
            var fileName = path[(path.LastIndexOf('/') + 1)..];
            var pciIds = PciIds(devices);
            var typeName = Display(Child(entry, "ComponentType")) ?? type;
            var categoryName = Display(Child(entry, "Category"));
            DateTimeOffset? released = DateTimeOffset.TryParse((string?)entry.Attribute("releaseDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
            var criticality = Display(Child(entry, "Criticality"));
            var lineage = LineageKey(devices, name);
            var component = new DriverVendorComponent(lineage, new DriverComponentRecord
            {
                ComponentId = packageId,
                Name = name,
                Category = categoryName is null ? typeName : $"{typeName} - {categoryName}",
                Version = (string?)entry.Attribute("dellVersion"),
                ReleasedAt = released,
                Size = long.TryParse((string?)entry.Attribute("size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                Sha256 = Sha256(entry),
                Url = DownloadBase + path.TrimStart('/'),
                FileName = fileName,
                AllowedActions = allowed,
                DefaultAction = defaultAction,

                // Only a 64-bit package that injects drivers is extracted; anything else runs as it is.
                Extract = packageType.Equals("LW64", StringComparison.OrdinalIgnoreCase) && allowed.Any(a => a is DriverAction.Offline or DriverAction.OfflineAndSpecialize) ? DriverExtractKind.Dell64 : DriverExtractKind.None,
                InstallCommand = specialize ? new DriverInstallCommand { Executable = fileName, Arguments = "/s" } : null,
                PciIds = pciIds,
                Details =
                [
                    new DriverDetailField { Label = "Installer", Value = packageType.Equals("LWXP", StringComparison.OrdinalIgnoreCase) ? "32-bit" : "64-bit" },
                    new DriverDetailField { Label = "Hardware match", Value = pciIds.Count == 0 ? "Any device" : $"{pciIds.Count} PCI {(pciIds.Count == 1 ? "device" : "devices")}" },
                    new DriverDetailField { Label = "Package type", Value = packageType.Length == 0 ? "Not specified" : packageType },
                    new DriverDetailField { Label = "Criticality", Value = criticality ?? "Not specified" },
                ],
                DescriptionUrl = (string?)Child(entry, "ImportantInfo")?.Attribute("URL"),
                Description = Truncate(Display(Child(entry, "Description")), 2000),
                AddedAt = seenAt,
                UpdatedAt = seenAt,
            });

            if (!newest.TryGetValue(lineage, out var known) || component.Component.ReleasedAt > known.Component.ReleasedAt)
            {
                newest[lineage] = component;
            }
        }

        return [.. newest.Values];
    }

    /// <summary>Firmware and BIOS are never stored (FR-003).</summary>
    public static bool IsExcluded(string name, string componentType) =>
        componentType.Equals("FRMW", StringComparison.OrdinalIgnoreCase)
        || componentType.Equals("BIOS", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Firmware", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Dell classification table of FR-017.</summary>
    public static (DriverAction Default, IReadOnlyList<DriverAction> Allowed) Classify(string componentType, string packageType, bool bundlesApp)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        ArgumentNullException.ThrowIfNull(packageType);
        var driver = componentType.Equals("DRVR", StringComparison.OrdinalIgnoreCase);
        var lw64 = packageType.Equals("LW64", StringComparison.OrdinalIgnoreCase);
        if (driver && lw64 && bundlesApp)
        {
            return (DriverAction.OfflineAndSpecialize, [DriverAction.OfflineAndSpecialize, DriverAction.Offline, DriverAction.Skip]);
        }

        if (driver && lw64)
        {
            return (DriverAction.Offline, [DriverAction.Offline, DriverAction.Skip]);
        }

        // A 32-bit package cannot run in WinPE, so it never injects offline.
        if (driver)
        {
            return (DriverAction.Specialize, [DriverAction.Specialize, DriverAction.Skip]);
        }

        return componentType.Equals("APAC", StringComparison.OrdinalIgnoreCase)
            ? (DriverAction.Skip, [DriverAction.Skip, DriverAction.Specialize])
            : (DriverAction.Skip, [DriverAction.Skip]);
    }

    // The first device's component ID keeps a lineage stable across packages; without one, the name does.
    private static string LineageKey(List<XElement> devices, string name)
    {
        var componentId = devices.Select(d => ((string?)d.Attribute("componentID"))?.Trim()).FirstOrDefault(id => !string.IsNullOrEmpty(id));
        if (componentId is not null)
        {
            return $"dell-{componentId}";
        }

        var normalised = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (normalised.Contains("--", StringComparison.Ordinal))
        {
            normalised = normalised.Replace("--", "-", StringComparison.Ordinal);
        }

        normalised = normalised.Trim('-');
        return $"name-{(normalised.Length > 70 ? normalised[..70] : normalised)}";
    }

    // A device listed only by a USB or other generic ID cannot be checked against PCI hardware, so
    // such a component carries no PCI IDs and is never skipped for missing hardware.
    private static List<DriverPciId> PciIds(List<XElement> devices)
    {
        if (devices.Count == 0 || devices.Any(d => !d.Elements().Any(e => e.Name.LocalName == "PCIInfo")))
        {
            return [];
        }

        return devices
            .SelectMany(d => d.Elements().Where(e => e.Name.LocalName == "PCIInfo"))
            .Select(p => new DriverPciId
            {
                Vendor = Hex((string?)p.Attribute("vendorID")) ?? string.Empty,
                Device = Hex((string?)p.Attribute("deviceID")) ?? string.Empty,
                SubVendor = Hex((string?)p.Attribute("subVendorID")),
                SubDevice = Hex((string?)p.Attribute("subDeviceID")),
            })
            .Where(p => p.Vendor.Length > 0 && p.Device.Length > 0)
            .Distinct()
            .ToList();
    }

    private static bool SupportsWindows(XElement entry, string windows, string architecture) =>
        Descendants(entry, "OperatingSystem").Any(os =>
            string.Equals((string?)os.Attribute("osArch"), architecture, StringComparison.OrdinalIgnoreCase)
            && Display(os)?.Contains(windows, StringComparison.OrdinalIgnoreCase) == true);

    private static string? Sha256(XElement element) =>
        Child(element, "Cryptography")?.Elements()
            .FirstOrDefault(h => h.Name.LocalName == "Hash" && string.Equals((string?)h.Attribute("algorithm"), "SHA256", StringComparison.OrdinalIgnoreCase))?.Value.Trim().ToLowerInvariant();

    private static string? Hex(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private static XElement? Child(XElement? element, string localName) =>
        element?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Descendants(XElement element, string localName) =>
        element.Descendants().Where(e => e.Name.LocalName == localName);

    private static string? Display(XElement? element) =>
        Child(element, "Display")?.Value.Trim() is { Length: > 0 } text ? text : null;

    private static string? Truncate(string? text, int max) => text is null || text.Length <= max ? text : text[..max];

    private static IEnumerable<XElement> ReadElements(Stream content, string localName)
    {
        using var reader = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, IgnoreWhitespace = true });
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == localName)
            {
                yield return (XElement)XNode.ReadFrom(reader);
                continue;
            }

            reader.Read();
        }
    }
}
