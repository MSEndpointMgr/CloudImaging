using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using CloudImaging.Contracts.Models.SetupProfiles;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>Index row of one stored setup profile version.</summary>
public sealed record SetupProfileVersionRecord
{
    /// <summary>Profile id.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Version number.</summary>
    public required int Version { get; init; }

    /// <summary>When the version was saved.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Who saved the version.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Blob that holds the version document.</summary>
    public required string BlobName { get; init; }

    /// <summary>Lowercase hex SHA-256 of the blob bytes.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Blob length in bytes.</summary>
    public long ContentSizeBytes { get; init; }

    /// <summary>Set when the version was created by Restore.</summary>
    public int? RestoredFrom { get; init; }

    /// <summary>Set on version 1 of a duplicated profile.</summary>
    public SetupProfileDuplicatedFrom? DuplicatedFrom { get; init; }

    /// <summary>What changed compared with the previous version.</summary>
    public SetupProfileChangeSummary ChangeSummary { get; init; } = new();
}

/// <summary>Serialized version document with its hash and size.</summary>
/// <param name="Bytes">UTF-8 JSON bytes.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of <paramref name="Bytes"/>.</param>
public sealed record SetupProfileVersionBlob(byte[] Bytes, string Sha256);

/// <summary>
/// Setup profile versions: an index row per version in table SetupProfileVersions
/// (PartitionKey: profileId | RowKey: version as six digits) and the immutable content as one
/// JSON blob per version in container setup-profiles.
/// </summary>
public sealed class SetupProfileVersionRepository
{
    /// <summary>Blob container that holds version documents.</summary>
    public const string ContainerName = "setup-profiles";

    private const string TableName = "SetupProfileVersions";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TableClient _table;
    private readonly BlobContainerClient _container;

    /// <param name="tableServiceClient">Table service client used to resolve the versions table.</param>
    /// <param name="container">Blob container for the version documents.</param>
    public SetupProfileVersionRepository(TableServiceClient tableServiceClient, BlobContainerClient container)
    {
        _table = tableServiceClient.GetTableClient(TableName);
        _container = container;
    }

    /// <summary>Blob name of a version document.</summary>
    public static string BlobNameFor(Guid profileId, int version) => $"{profileId:D}/{version:D6}.json";

    /// <summary>Serializes a version document and computes its hash.</summary>
    public static SetupProfileVersionBlob Serialize(SetupProfileContent content)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(content, JsonOptions);
        return new SetupProfileVersionBlob(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Uploads a version document. Fails with 409 when the version already exists, so a version is never overwritten.</summary>
    public async Task UploadAsync(string blobName, SetupProfileVersionBlob blob, CancellationToken ct = default)
    {
        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
        };
        await _container.GetBlobClient(blobName).UploadAsync(new BinaryData(blob.Bytes), options, ct);
    }

    /// <summary>Downloads and parses a version document, or returns null when the blob is missing.</summary>
    public async Task<SetupProfileContent?> DownloadAsync(string blobName, CancellationToken ct = default)
    {
        try
        {
            var result = await _container.GetBlobClient(blobName).DownloadContentAsync(ct);
            return result.Value.Content.ToObjectFromJson<SetupProfileContent>(JsonOptions);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>True when the version document exists.</summary>
    public async Task<bool> BlobExistsAsync(string blobName, CancellationToken ct = default) =>
        (await _container.GetBlobClient(blobName).ExistsAsync(ct)).Value;

    /// <summary>Adds a version index row.</summary>
    public async Task AddAsync(SetupProfileVersionRecord record, CancellationToken ct = default) =>
        await _table.AddEntityAsync(ToEntity(record), ct);

    /// <summary>Returns a version index row, or null.</summary>
    public async Task<SetupProfileVersionRecord?> GetAsync(Guid profileId, int version, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(profileId.ToString(), version.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>Lists a profile's version rows, newest first.</summary>
    public async Task<List<SetupProfileVersionRecord>> ListAsync(Guid profileId, CancellationToken ct = default)
    {
        var partition = profileId.ToString();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition}");
        var versions = new List<SetupProfileVersionRecord>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            versions.Add(FromEntity(entity));
        }
        versions.Sort((a, b) => b.Version.CompareTo(a.Version));
        return versions;
    }

    /// <summary>Deletes a version: the blob first, then the row, so a row never points at nothing for long.</summary>
    public async Task DeleteAsync(SetupProfileVersionRecord record, CancellationToken ct = default)
    {
        await _container.GetBlobClient(record.BlobName).DeleteIfExistsAsync(cancellationToken: ct);
        try
        {
            await _table.DeleteEntityAsync(record.ProfileId.ToString(), record.Version.ToString("D6", System.Globalization.CultureInfo.InvariantCulture), cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private static TableEntity ToEntity(SetupProfileVersionRecord r) => new(r.ProfileId.ToString(), r.Version.ToString("D6", System.Globalization.CultureInfo.InvariantCulture))
    {
        ["CreatedAt"] = r.CreatedAt,
        ["CreatedBy"] = r.CreatedBy,
        ["BlobName"] = r.BlobName,
        ["ContentSha256"] = r.ContentSha256,
        ["ContentSizeBytes"] = r.ContentSizeBytes,
        ["RestoredFrom"] = r.RestoredFrom,
        ["DuplicatedFromJson"] = r.DuplicatedFrom is null ? null : JsonSerializer.Serialize(r.DuplicatedFrom, JsonOptions),
        ["ChangeSummaryJson"] = JsonSerializer.Serialize(r.ChangeSummary, JsonOptions),
    };

    private static SetupProfileVersionRecord FromEntity(TableEntity e) => new()
    {
        ProfileId = Guid.Parse(e.PartitionKey),
        Version = int.Parse(e.RowKey, System.Globalization.CultureInfo.InvariantCulture),
        CreatedAt = e.GetDateTimeOffset("CreatedAt") ?? DateTimeOffset.MinValue,
        CreatedBy = e.GetString("CreatedBy"),
        BlobName = e.GetString("BlobName") ?? string.Empty,
        ContentSha256 = e.GetString("ContentSha256") ?? string.Empty,
        ContentSizeBytes = e.GetInt64("ContentSizeBytes") ?? 0,
        RestoredFrom = e.GetInt32("RestoredFrom"),
        DuplicatedFrom = Deserialize<SetupProfileDuplicatedFrom>(e.GetString("DuplicatedFromJson")),
        ChangeSummary = Deserialize<SetupProfileChangeSummary>(e.GetString("ChangeSummaryJson")) ?? new(),
    };

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException) { return null; }
    }
}
