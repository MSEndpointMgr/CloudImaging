using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for <see cref="PreFlightOverride"/> entities.
/// PartitionKey: "override" | RowKey: hex of the normalized serial number, so a device has at most
/// one override and serials containing characters Table Storage forbids in keys still work.
/// </summary>
public sealed class PreFlightOverrideRepository
{
    private const string TableName = "PreFlightOverrides";
    private const string Partition = "override";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TableClient _table;

    /// <param name="tableServiceClient">Table service client used to resolve the pre-flight overrides table.</param>
    public PreFlightOverrideRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    /// <summary>Creates the backing table if it does not already exist.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Stores an override, replacing any earlier one for the same serial.</summary>
    public async Task UpsertAsync(PreFlightOverride preFlightOverride, CancellationToken ct = default) =>
        await _table.UpsertEntityAsync(ToEntity(preFlightOverride), TableUpdateMode.Replace, ct);

    /// <summary>Returns a single override and its ETag for a device serial number, or null if none exists.</summary>
    public async Task<(PreFlightOverride Override, ETag ETag)?> GetAsync(string serialNumber, CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, KeyFor(serialNumber), cancellationToken: ct);
            return (FromEntity(response.Value), response.Value.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Lists all active pre-flight overrides.</summary>
    public IAsyncEnumerable<PreFlightOverride> ListAsync(CancellationToken ct = default)
    {
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {Partition}");
        return _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).Select(FromEntity);
    }

    /// <summary>
    /// Deletes the override only if it is unchanged since <paramref name="etag"/> was read, so two
    /// sessions starting at once cannot both use it. Returns false when another caller got there first.
    /// </summary>
    public async Task<bool> TryConsumeAsync(string serialNumber, ETag etag, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, KeyFor(serialNumber), etag, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return false;
        }
    }

    /// <summary>Removes the override for a serial. Returns false when there was none.</summary>
    public async Task<bool> DeleteAsync(string serialNumber, CancellationToken ct = default)
    {
        try
        {
            await _table.DeleteEntityAsync(Partition, KeyFor(serialNumber), cancellationToken: ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    internal static string NormalizeSerial(string serialNumber) => serialNumber.Trim().ToUpperInvariant();

    internal static string KeyFor(string serialNumber) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(NormalizeSerial(serialNumber)));

    internal static TableEntity ToEntity(PreFlightOverride o) => new(Partition, KeyFor(o.SerialNumber))
    {
        ["SerialNumber"] = o.SerialNumber,
        ["CoveredChecks"] = string.Join(',', o.CoveredChecks),
        ["SourceSessionId"] = o.SourceSessionId.ToString(),
        ["DeviceManufacturer"] = o.DeviceManufacturer,
        ["DeviceModel"] = o.DeviceModel,
        ["LocationName"] = o.LocationName,
        ["SourceChecksJson"] = SerializeChecks(o.SourceChecks),
        ["ApprovedBy"] = o.ApprovedBy,
        ["ApprovedByObjectId"] = o.ApprovedByObjectId,
        ["ApprovedAt"] = o.ApprovedAt,
        ["ExpiresAt"] = o.ExpiresAt,
    };

    internal static PreFlightOverride FromEntity(TableEntity e) => new()
    {
        SerialNumber = e.GetString("SerialNumber") ?? string.Empty,
        CoveredChecks = (e.GetString("CoveredChecks") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => Enum.TryParse<PreFlightCheck>(name, out var check) ? check : (PreFlightCheck?)null)
            .OfType<PreFlightCheck>()
            .ToList(),
        SourceSessionId = Guid.TryParse(e.GetString("SourceSessionId"), out var source) ? source : Guid.Empty,
        DeviceManufacturer = e.GetString("DeviceManufacturer") ?? string.Empty,
        DeviceModel = e.GetString("DeviceModel") ?? string.Empty,
        LocationName = e.GetString("LocationName"),
        SourceChecks = DeserializeChecks(e.GetString("SourceChecksJson")),
        ApprovedBy = e.GetString("ApprovedBy") ?? string.Empty,
        ApprovedByObjectId = e.GetString("ApprovedByObjectId"),
        ApprovedAt = e.GetDateTimeOffset("ApprovedAt") ?? DateTimeOffset.MinValue,
        ExpiresAt = e.GetDateTimeOffset("ExpiresAt") ?? DateTimeOffset.MinValue,
    };

    internal static IReadOnlyList<PreFlightCheckResult> DeserializeChecks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PreFlightCheckResult>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string SerializeChecks(IReadOnlyList<PreFlightCheckResult> checks) =>
        JsonSerializer.Serialize(checks, JsonOptions);
}
