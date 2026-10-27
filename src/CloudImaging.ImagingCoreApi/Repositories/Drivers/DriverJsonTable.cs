using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace CloudImaging.ImagingCoreApi.Repositories.Drivers;

/// <summary>A row value with the ETag it was read with.</summary>
/// <typeparam name="T">Row type.</typeparam>
/// <param name="RowKey">Row key.</param>
/// <param name="Value">Row value.</param>
/// <param name="ETag">ETag for conditional writes.</param>
public sealed record Stored<T>(string RowKey, T Value, ETag ETag);

/// <summary>
/// A driver table whose rows hold one typed record as JSON in a <c>Data</c> column. Keys follow
/// data-model.md section 3, so every read is a point read or a partition scan and no row needs
/// queryable columns. Conflicts surface as false returns: 409 on insert, 412 on a stale ETag.
/// </summary>
/// <typeparam name="T">Row type.</typeparam>
public sealed class DriverJsonTable<T>
    where T : class
{
    private const string DataColumn = "Data";
    private const int MaxBatch = 100;
    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service.</param>
    /// <param name="tableName">Table name.</param>
    public DriverJsonTable(TableServiceClient tableServiceClient, string tableName)
    {
        Name = tableName;
        _table = tableServiceClient.GetTableClient(tableName);
    }

    /// <summary>Table name.</summary>
    public string Name { get; }

    /// <summary>Creates the table when missing.</summary>
    public async Task EnsureExistsAsync(CancellationToken ct = default) => await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Reads one row, or null when missing.</summary>
    public async Task<Stored<T>?> GetAsync(string partitionKey, string rowKey, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(partitionKey, rowKey, cancellationToken: ct);
            return Read(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Inserts a row; false when it already exists.</summary>
    public async Task<bool> TryAddAsync(string partitionKey, string rowKey, T value, CancellationToken ct = default)
    {
        try
        {
            await _table.AddEntityAsync(Write(partitionKey, rowKey, value), ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    /// <summary>Replaces a row if it still has <paramref name="etag"/>; false when it changed or is gone.</summary>
    public async Task<bool> TryUpdateAsync(string partitionKey, string rowKey, T value, ETag etag, CancellationToken ct = default)
    {
        try
        {
            await _table.UpdateEntityAsync(Write(partitionKey, rowKey, value), etag, TableUpdateMode.Replace, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    /// <summary>Inserts or replaces a row.</summary>
    public async Task UpsertAsync(string partitionKey, string rowKey, T value, CancellationToken ct = default) =>
        await _table.UpsertEntityAsync(Write(partitionKey, rowKey, value), TableUpdateMode.Replace, ct);

    /// <summary>Inserts or replaces many rows of one partition, 100 per transaction.</summary>
    public async Task UpsertManyAsync(string partitionKey, IEnumerable<(string RowKey, T Value)> rows, CancellationToken ct = default)
    {
        foreach (var chunk in rows.Chunk(MaxBatch))
        {
            await _table.SubmitTransactionAsync(chunk.Select(row => new TableTransactionAction(TableTransactionActionType.UpsertReplace, Write(partitionKey, row.RowKey, row.Value))), ct);
        }
    }

    /// <summary>Deletes many rows of one partition, 100 per transaction.</summary>
    public async Task DeleteManyAsync(string partitionKey, IEnumerable<string> rowKeys, CancellationToken ct = default)
    {
        foreach (var chunk in rowKeys.Chunk(MaxBatch))
        {
            await _table.SubmitTransactionAsync(chunk.Select(rowKey => new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(partitionKey, rowKey), ETag.All)), ct);
        }
    }

    /// <summary>Deletes a row; false when it was missing or changed since <paramref name="etag"/>.</summary>
    public async Task<bool> DeleteAsync(string partitionKey, string rowKey, ETag? etag = null, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(partitionKey, rowKey, etag ?? ETag.All, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    /// <summary>Reads every row of a partition, ordered by row key.</summary>
    public async Task<List<Stored<T>>> ListPartitionAsync(string partitionKey, CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partitionKey}");
        var rows = new List<Stored<T>>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct))
        {
            if (Read(entity) is { } row)
            {
                rows.Add(row);
            }
        }

        rows.Sort((a, b) => string.CompareOrdinal(a.RowKey, b.RowKey));
        return rows;
    }

    /// <summary>Reads every row of the table with its partition key.</summary>
    public async Task<List<(string PartitionKey, Stored<T> Row)>> ListAllAsync(CancellationToken ct = default)
    {
        var rows = new List<(string, Stored<T>)>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(cancellationToken: ct))
        {
            if (Read(entity) is { } row)
            {
                rows.Add((entity.PartitionKey, row));
            }
        }

        return rows;
    }

    private static TableEntity Write(string partitionKey, string rowKey, T value) => new(partitionKey, rowKey)
    {
        [DataColumn] = JsonSerializer.Serialize(value, DriverJson.Options),
    };

    private static Stored<T>? Read(TableEntity entity)
    {
        var json = entity.GetString(DataColumn);
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var value = JsonSerializer.Deserialize<T>(json, DriverJson.Options);
        return value is null ? null : new Stored<T>(entity.RowKey, value, entity.ETag);
    }
}

/// <summary>JSON settings shared by driver rows, blobs and queue messages.</summary>
public static class DriverJson
{
    /// <summary>camelCase, string enums.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
