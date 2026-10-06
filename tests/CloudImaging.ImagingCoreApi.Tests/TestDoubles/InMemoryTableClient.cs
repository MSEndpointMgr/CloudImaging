using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Moq;

namespace CloudImaging.ImagingCoreApi.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="TableClient"/> for exercising repositories end to end: ETag
/// concurrency, 404/409/412 semantics, and the simple "Prop eq 'value' and ..." filters the
/// repositories build with <see cref="TableClient.CreateQueryFilter(FormattableString)"/>.
/// </summary>
internal sealed partial class InMemoryTableClient : TableClient
{
    private readonly Dictionary<(string Pk, string Rk), TableEntity> _rows = new();
    private int _version;

    public IReadOnlyCollection<TableEntity> Rows => _rows.Values;

    public override Task<Response<Azure.Data.Tables.Models.TableItem>> CreateIfNotExistsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Response.FromValue(new Azure.Data.Tables.Models.TableItem("t"), new FakeResponse(204)));

    public override Task<Response> AddEntityAsync<T>(T entity, CancellationToken cancellationToken = default)
    {
        var key = (entity.PartitionKey, entity.RowKey);
        if (_rows.ContainsKey(key))
        {
            throw new RequestFailedException(409, "Conflict");
        }
        return Task.FromResult<Response>(Store(entity));
    }

    public override Task<Response<T>> GetEntityAsync<T>(string partitionKey, string rowKey, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        if (!_rows.TryGetValue((partitionKey, rowKey), out var row))
        {
            throw new RequestFailedException(404, "Not found");
        }
        return Task.FromResult(Response.FromValue((T)(object)Clone(row), new FakeResponse(200)));
    }

    public override Task<Response> UpdateEntityAsync<T>(T entity, ETag ifMatch, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default)
    {
        if (!_rows.TryGetValue((entity.PartitionKey, entity.RowKey), out var existing))
        {
            throw new RequestFailedException(404, "Not found");
        }
        if (ifMatch != ETag.All && ifMatch != existing.ETag)
        {
            throw new RequestFailedException(412, "Precondition failed");
        }
        return Task.FromResult<Response>(Store(entity));
    }

    public override Task<Response> UpsertEntityAsync<T>(T entity, TableUpdateMode mode = TableUpdateMode.Merge, CancellationToken cancellationToken = default) =>
        Task.FromResult<Response>(Store(entity));

    public override Task<Response> DeleteEntityAsync(string partitionKey, string rowKey, ETag ifMatch = default, CancellationToken cancellationToken = default)
    {
        if (!_rows.Remove((partitionKey, rowKey)))
        {
            throw new RequestFailedException(404, "Not found");
        }
        return Task.FromResult<Response>(new FakeResponse(204));
    }

    public override AsyncPageable<T> QueryAsync<T>(string? filter = null, int? maxPerPage = null, IEnumerable<string>? select = null, CancellationToken cancellationToken = default)
    {
        var conditions = FilterTerm().Matches(filter ?? string.Empty)
            .Select(m => (Property: m.Groups[1].Value, Value: m.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal)))
            .ToList();
        var matches = _rows.Values
            .Where(row => conditions.All(c => Read(row, c.Property) == c.Value))
            .Select(row => (T)(object)Clone(row))
            .ToList();
        return AsyncPageable<T>.FromPages([Page<T>.FromValues(matches, null, new FakeResponse(200))]);
    }

    private FakeResponse Store(ITableEntity entity)
    {
        var copy = new TableEntity(entity.PartitionKey, entity.RowKey);
        if (entity is TableEntity source)
        {
            foreach (var pair in source)
            {
                if (pair.Key is not ("PartitionKey" or "RowKey" or "odata.etag" or "Timestamp") && pair.Value is not null)
                {
                    copy[pair.Key] = pair.Value;
                }
            }
        }
        _version++;
        copy.ETag = new ETag($"W/\"{_version}\"");
        _rows[(copy.PartitionKey, copy.RowKey)] = copy;
        return new FakeResponse(204, copy.ETag.ToString());
    }

    private static string? Read(TableEntity row, string property) => property switch
    {
        "PartitionKey" => row.PartitionKey,
        "RowKey" => row.RowKey,
        _ => row.TryGetValue(property, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null,
    };

    private static TableEntity Clone(TableEntity row)
    {
        var copy = new TableEntity(row.PartitionKey, row.RowKey) { ETag = row.ETag };
        foreach (var pair in row)
        {
            if (pair.Key is not ("PartitionKey" or "RowKey" or "odata.etag"))
            {
                copy[pair.Key] = pair.Value;
            }
        }
        return copy;
    }

    [GeneratedRegex(@"(\w+) eq '((?:[^']|'')*)'")]
    private static partial Regex FilterTerm();

    /// <summary>Builds a <see cref="TableServiceClient"/> that hands out one in-memory table per name.</summary>
    public static (TableServiceClient Service, Func<string, InMemoryTableClient> Table) CreateService()
    {
        var tables = new Dictionary<string, InMemoryTableClient>();
        InMemoryTableClient Get(string name)
        {
            if (!tables.TryGetValue(name, out var table))
            {
                table = new InMemoryTableClient();
                tables[name] = table;
            }
            return table;
        }

        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Returns((string name) => Get(name));
        return (service.Object, Get);
    }
}

/// <summary>Concrete <see cref="Response"/> carrying a status and optional ETag header.</summary>
internal sealed class FakeResponse(int status, string? etag = null) : Response
{
    public override int Status => status;
    public override string ReasonPhrase => string.Empty;
    public override Stream? ContentStream { get; set; }
    public override string ClientRequestId { get; set; } = string.Empty;

    public override void Dispose()
    {
    }

    protected override bool ContainsHeader(string name) => etag is not null && name == "ETag";

    protected override IEnumerable<HttpHeader> EnumerateHeaders() =>
        etag is null ? [] : [new HttpHeader("ETag", etag)];

    protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
    {
        value = name == "ETag" ? etag : null;
        return value is not null;
    }

    protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
    {
        values = name == "ETag" && etag is not null ? [etag] : null;
        return values is not null;
    }
}
