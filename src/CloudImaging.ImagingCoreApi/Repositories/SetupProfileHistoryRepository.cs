using System.Globalization;
using System.Text.Json;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>One stored profile-level history event.</summary>
public sealed record SetupProfileHistoryRecord
{
    /// <summary>Profile id.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>What happened.</summary>
    public required SetupProfileHistoryKind Kind { get; init; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>UPN of the user who did it.</summary>
    public string? By { get; init; }

    /// <summary>Entra object id of the user who did it.</summary>
    public string? ByObjectId { get; init; }

    /// <summary>Ready-to-show sentence.</summary>
    public string Change { get; init; } = string.Empty;

    /// <summary>Locations added, for targeting events.</summary>
    public IReadOnlyList<Guid> AddedLocationIds { get; init; } = [];

    /// <summary>Locations removed, for targeting events.</summary>
    public IReadOnlyList<Guid> RemovedLocationIds { get; init; } = [];

    /// <summary>Version, for version events.</summary>
    public int? Version { get; init; }
}

/// <summary>
/// Profile-level history. PartitionKey: profileId | RowKey: inverted ticks plus a unique suffix, so
/// a partition query returns newest first. Never pruned by version retention and kept after the
/// profile is deleted.
/// </summary>
public sealed class SetupProfileHistoryRepository
{
    private const string TableName = "SetupProfileHistory";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the history table.</param>
    public SetupProfileHistoryRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Adds a history event.</summary>
    public async Task AddAsync(SetupProfileHistoryRecord record, CancellationToken ct = default)
    {
        var rowKey = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.MaxValue.UtcTicks - record.At.UtcTicks:D19}-{Guid.NewGuid():N}");
        var entity = new TableEntity(record.ProfileId.ToString(), rowKey)
        {
            ["Kind"] = record.Kind.ToString(),
            ["At"] = record.At,
            ["By"] = record.By,
            ["ByObjectId"] = record.ByObjectId,
            ["Change"] = record.Change,
            ["AddedLocationIdsJson"] = record.AddedLocationIds.Count == 0 ? null : JsonSerializer.Serialize(record.AddedLocationIds, JsonOptions),
            ["RemovedLocationIdsJson"] = record.RemovedLocationIds.Count == 0 ? null : JsonSerializer.Serialize(record.RemovedLocationIds, JsonOptions),
            ["Version"] = record.Version,
        };
        await _table.AddEntityAsync(entity, ct);
    }

    /// <summary>Lists a profile's history, newest first.</summary>
    public async Task<List<SetupProfileHistoryRecord>> ListAsync(Guid profileId, CancellationToken ct = default)
    {
        var partition = profileId.ToString();
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition}");
        var rows = new List<(string RowKey, SetupProfileHistoryRecord Record)>();
        await foreach (var e in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            rows.Add((e.RowKey, new SetupProfileHistoryRecord
            {
                ProfileId = profileId,
                Kind = Enum.TryParse<SetupProfileHistoryKind>(e.GetString("Kind"), out var kind) ? kind : SetupProfileHistoryKind.VersionSaved,
                At = e.GetDateTimeOffset("At") ?? DateTimeOffset.MinValue,
                By = e.GetString("By"),
                ByObjectId = e.GetString("ByObjectId"),
                Change = e.GetString("Change") ?? string.Empty,
                AddedLocationIds = ReadIds(e.GetString("AddedLocationIdsJson")),
                RemovedLocationIds = ReadIds(e.GetString("RemovedLocationIdsJson")),
                Version = e.GetInt32("Version"),
            }));
        }
        return [.. rows.OrderBy(r => r.RowKey, StringComparer.Ordinal).Select(r => r.Record)];
    }

    private static List<Guid> ReadIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions) ?? [];
        }
        catch (JsonException) { return []; }
    }
}
