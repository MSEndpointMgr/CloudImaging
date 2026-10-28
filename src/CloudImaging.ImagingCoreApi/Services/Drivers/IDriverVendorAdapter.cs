using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>A model read from a vendor's model list.</summary>
/// <param name="Key">Catalog model key (row key of <c>DriverCatalogModels</c>).</param>
/// <param name="Model">The model.</param>
public sealed record DriverVendorModel(string Key, DriverCatalogModelRecord Model);

/// <summary>A component read from a vendor's component catalog.</summary>
/// <param name="LineageKey">Key that stays the same across versions.</param>
/// <param name="Component">The component with its classification.</param>
public sealed record DriverVendorComponent(string LineageKey, DriverComponentRecord Component);

/// <summary>
/// Everything that differs between vendors (FR-001, contracts/vendor-catalogs.md): where the
/// catalogs are, how they are parsed, what is excluded, how components are classified and how
/// lineages are keyed. Policy, sync, storage, matching and the Client do not depend on the vendor.
/// </summary>
public interface IDriverVendorAdapter
{
    /// <summary>The vendor.</summary>
    DriverVendor Vendor { get; }

    /// <summary>How devices are matched, for the Vendors card ("system ID").</summary>
    string MatchKeyType { get; }

    /// <summary>The catalog the model list comes from.</summary>
    string ModelListUrl { get; }

    /// <summary>Reads the decompressed model list.</summary>
    IReadOnlyList<DriverVendorModel> ParseModelList(Stream content, DateTimeOffset seenAt);

    /// <summary>Component catalogs to read for a model and release.</summary>
    IReadOnlyList<string> ComponentCatalogUrls(DriverCatalogModelRecord model, WindowsRelease release);

    /// <summary>Reads one decompressed component catalog, without excluded entries.</summary>
    IReadOnlyList<DriverVendorComponent> ParseComponents(Stream content, DriverCatalogModelRecord model, WindowsRelease release, DateTimeOffset seenAt);

    /// <summary>The device's match key for this vendor (HP system ID, Dell system SKU), or null when the device reports none.</summary>
    string? DeviceMatchKey(CloudImaging.Contracts.Models.DeviceHardwareMetadata? hardware);

    /// <summary>True when a device manufacturer string names this vendor, so its key is tried first.</summary>
    bool IsVendorOf(string? manufacturer);

    /// <summary>The SHA-256 the vendor publishes for a component catalog, or null when it publishes none.</summary>
    string? CatalogSha256(DriverCatalogModelRecord model, string url) => null;
}
