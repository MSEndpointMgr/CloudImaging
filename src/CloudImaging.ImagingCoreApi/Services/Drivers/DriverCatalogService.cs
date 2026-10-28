using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>A decompressed catalog, or why it could not be read.</summary>
/// <param name="Content">Decompressed catalog.</param>
/// <param name="Failure">Reason it could not be read.</param>
public sealed record DriverCatalogRead(MemoryStream? Content, string? Failure);

/// <summary>Result of reading a vendor's model list.</summary>
/// <param name="NewModels">Models added to the stored list.</param>
/// <param name="Failure">Reason the list could not be read; the stored list is unchanged.</param>
public sealed record DriverModelListRefresh(int NewModels, string? Failure);

/// <summary>
/// Reads vendor catalogs: downloads them through the host allowlist, keeps a copy in the
/// <c>drivers</c> container (preview reads it for 24 hours), decompresses cabinets and hands the
/// content to the vendor's adapter. Also keeps the stored vendor model lists (data-model 3.3).
/// </summary>
public sealed class DriverCatalogService
{
    /// <summary>How long preview reuses a stored catalog.</summary>
    public static readonly TimeSpan PreviewCacheAge = TimeSpan.FromHours(24);

    /// <summary>A stored model list older than this is read again when the Onboard model dialog opens.</summary>
    public static readonly TimeSpan ModelListFreshness = TimeSpan.FromDays(7);

    private const string FetchedAtKey = "fetchedAt";
    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverStorageService _storage;
    private readonly BlobContainerClient _container;
    private readonly IReadOnlyList<IDriverVendorAdapter> _adapters;
    private readonly TimeProvider _time;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="storage">Vendor downloads.</param>
    /// <param name="container">The <c>drivers</c> container.</param>
    /// <param name="adapters">Vendor adapters.</param>
    /// <param name="time">Clock.</param>
    public DriverCatalogService(DriverStore store, DriverModelRepository models, DriverStorageService storage, BlobContainerClient container, IEnumerable<IDriverVendorAdapter> adapters, TimeProvider time)
    {
        _store = store;
        _models = models;
        _storage = storage;
        _container = container;
        _adapters = adapters.ToList();
        _time = time;
    }

    /// <summary>Adapters of supported vendors.</summary>
    public IReadOnlyList<IDriverVendorAdapter> Adapters => _adapters;

    /// <summary>The adapter of a vendor, or null when the vendor is not supported.</summary>
    public IDriverVendorAdapter? Adapter(DriverVendor vendor) => _adapters.FirstOrDefault(a => a.Vendor == vendor);

    /// <summary>"Can't reach catalog at https://host." for a catalog URL.</summary>
    public static string CannotReach(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? $"Can't reach catalog at {uri.Scheme}://{uri.Host}."
            : "Can't reach the vendor catalog.";

    /// <summary>
    /// Reads a catalog. With <paramref name="maxAge"/>, a stored copy younger than that is used;
    /// otherwise the vendor is asked and the stored copy replaced.
    /// </summary>
    public async Task<DriverCatalogRead> ReadAsync(DriverVendor vendor, string url, TimeSpan? maxAge, string? expectedSha256 = null, CancellationToken ct = default)
    {
        var blob = _container.GetBlobClient($"catalogs/{vendor}/{Hash(url)}.cab");
        if (maxAge is { } age && await ReadCachedAsync(blob, age, ct) is { } cached && Matches(cached, expectedSha256))
        {
            return Decompress(cached, url);
        }

        var (content, failure) = await _storage.DownloadCatalogAsync(url, ct);
        if (content is null)
        {
            return new DriverCatalogRead(null, $"{CannotReach(url)} {failure}".TrimEnd());
        }

        // A catalog that does not match the checksum its vendor published is never stored or read.
        if (!Matches(content, expectedSha256))
        {
            return new DriverCatalogRead(null, $"The catalog at {url} does not match its published checksum.");
        }

        var options = new BlobUploadOptions
        {
            Metadata = new Dictionary<string, string>
            {
                [FetchedAtKey] = _time.GetUtcNow().ToString("O"),
                ["sourceUrl"] = url,
            },
        };
        await blob.UploadAsync(BinaryData.FromBytes(content), options, ct);
        return Decompress(content, url);
    }

    /// <summary>
    /// Reads a vendor's model list and replaces the stored one: new and changed models are
    /// written, models the vendor no longer lists are deleted unless onboarded. A failure leaves
    /// the stored list as it was and records the error on the list row.
    /// </summary>
    public async Task<DriverModelListRefresh> RefreshModelListAsync(IDriverVendorAdapter adapter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var now = _time.GetUtcNow();
        var vendor = adapter.Vendor.ToString();
        var listRow = await _store.CatalogLists.GetAsync(DriverStore.ListPartition, vendor, ct);
        var read = await ReadAsync(adapter.Vendor, adapter.ModelListUrl, null, ct: ct);
        IReadOnlyList<DriverVendorModel>? parsed = null;
        var failure = read.Failure;
        if (read.Content is { } content)
        {
            await using (content)
            {
                try
                {
                    parsed = adapter.ParseModelList(content, now);
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or FormatException)
                {
                    failure = $"The model list at {adapter.ModelListUrl} could not be read.";
                }
            }
        }

        if (parsed is null)
        {
            await _store.CatalogLists.UpsertAsync(DriverStore.ListPartition, vendor, (listRow?.Value ?? new DriverCatalogListRecord { CatalogUrl = adapter.ModelListUrl }) with
            {
                CatalogUrl = adapter.ModelListUrl,
                LastAttemptAt = now,
                LastError = failure,
            }, ct);
            return new DriverModelListRefresh(0, failure);
        }

        var existing = (await _store.CatalogModels.ListPartitionAsync(vendor, ct)).ToDictionary(row => row.RowKey, row => row.Value, StringComparer.Ordinal);
        var onboarded = (await _models.ListAsync(ct)).Where(m => m.Vendor == adapter.Vendor).Select(m => m.CatalogModelKey).ToHashSet(StringComparer.Ordinal);
        var seen = parsed.GroupBy(m => m.Key, StringComparer.Ordinal).Select(g => g.Last()).ToList();

        // Only changed rows are written, so a daily sync of an unchanged list costs one read.
        var changed = seen.Where(m => !existing.TryGetValue(m.Key, out var old) || !SameModel(old, m.Model)).Select(m => (m.Key, m.Model));
        await _store.CatalogModels.UpsertManyAsync(vendor, changed, ct);
        var seenKeys = seen.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        await _store.CatalogModels.DeleteManyAsync(vendor, existing.Keys.Where(key => !seenKeys.Contains(key) && !onboarded.Contains(key)), ct);

        await _store.CatalogLists.UpsertAsync(DriverStore.ListPartition, vendor, new DriverCatalogListRecord
        {
            CatalogUrl = adapter.ModelListUrl,
            FetchedAt = now,
            ModelCount = seen.Count,
            LastAttemptAt = now,
            LastError = null,
        }, ct);
        return new DriverModelListRefresh(seen.Count(m => !existing.ContainsKey(m.Key)), null);
    }

    /// <summary>Reads every component catalog of a model and release.</summary>
    public async Task<(IReadOnlyList<DriverVendorComponent>? Components, string? Failure)> ReadComponentsAsync(IDriverVendorAdapter adapter, DriverCatalogModelRecord model, WindowsRelease release, TimeSpan? maxAge, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var components = new Dictionary<string, DriverVendorComponent>(StringComparer.Ordinal);
        foreach (var url in adapter.ComponentCatalogUrls(model, release))
        {
            var read = await ReadAsync(adapter.Vendor, url, maxAge, adapter.CatalogSha256(model, url), ct);
            if (read.Content is not { } content)
            {
                return (null, read.Failure);
            }

            await using (content)
            {
                try
                {
                    // Several catalogs (one per Dell SKU) can list the same lineage; the newest wins.
                    foreach (var component in adapter.ParseComponents(content, model, release, _time.GetUtcNow()))
                    {
                        if (!components.TryGetValue(component.LineageKey, out var known) || component.Component.ReleasedAt > known.Component.ReleasedAt)
                        {
                            components[component.LineageKey] = component;
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Xml.XmlException or InvalidDataException or FormatException or System.Text.Json.JsonException)
                {
                    return (null, $"The catalog at {url} could not be read.");
                }
            }
        }

        return (components.Values.ToList(), null);
    }

    private async Task<byte[]?> ReadCachedAsync(BlobClient blob, TimeSpan maxAge, CancellationToken ct)
    {
        try
        {
            var properties = await blob.GetPropertiesAsync(cancellationToken: ct);
            if (!properties.Value.Metadata.TryGetValue(FetchedAtKey, out var text)
                || !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var fetchedAt)
                || _time.GetUtcNow() - fetchedAt >= maxAge)
            {
                return null;
            }

            return (await blob.DownloadContentAsync(ct)).Value.Content.ToArray();
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    // Cabinets are expanded; anything else (a plain XML or JSON catalog) is used as it is.
    private static DriverCatalogRead Decompress(byte[] content, string url)
    {
        if (content.Length < 4 || content[0] != (byte)'M' || content[1] != (byte)'S' || content[2] != (byte)'C' || content[3] != (byte)'F')
        {
            return new DriverCatalogRead(new MemoryStream(content, writable: false), null);
        }

        var output = new MemoryStream();
        try
        {
            CabinetReader.ExtractSingleFile(content, output);
        }
        catch (InvalidDataException)
        {
            output.Dispose();
            return new DriverCatalogRead(null, $"The catalog at {url} could not be read.");
        }

        output.Position = 0;
        return new DriverCatalogRead(output, null);
    }

    private static bool SameModel(DriverCatalogModelRecord a, DriverCatalogModelRecord b) =>
        a.Name == b.Name
        && a.Architecture == b.Architecture
        && a.MatchKeys.SequenceEqual(b.MatchKeys)
        && a.Releases.SequenceEqual(b.Releases)
        && a.SkuCatalogs.SequenceEqual(b.SkuCatalogs);

    private static bool Matches(byte[] content, string? expectedSha256) =>
        expectedSha256 is null || string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), expectedSha256, StringComparison.OrdinalIgnoreCase);

    private static string Hash(string url) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)));
}
