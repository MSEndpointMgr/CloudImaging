using System.Text.Json;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Persists the tenant's single <see cref="RegionConfiguration"/> row (PartitionKey="regions",
/// RowKey="default"). <see cref="RegionConfiguration.Default"/> is returned until one is saved.
/// </summary>
public sealed partial class RegionConfigurationRepository
{
    private const string TableName = "RegionConfiguration";
    private const string PartitionKey = "regions";
    private const string RowKey = "default";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TableClient _table;
    private readonly ILogger<RegionConfigurationRepository> _logger;

    /// <param name="tableService">Table service client used to resolve the region table.</param>
    /// <param name="logger">Logger for this repository.</param>
    public RegionConfigurationRepository(TableServiceClient tableService, ILogger<RegionConfigurationRepository> logger)
    {
        _table = tableService.GetTableClient(TableName);
        _logger = logger;
    }

    /// <summary>Ensures the backing table exists. Called once on startup.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Returns the saved configuration, or the built-in default when none is saved.</summary>
    public async Task<RegionConfiguration> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(PartitionKey, RowKey, cancellationToken: ct);
            var e = response.Value;
            return new RegionConfiguration
            {
                Regions = Deserialize<List<RegionDefinition>>(e.GetString("RegionsJson")) ?? [],
                CountryOverrides = Deserialize<Dictionary<string, string?>>(e.GetString("CountryOverridesJson")) ?? [],
                LastModifiedAt = e.GetDateTimeOffset("LastModifiedAt") ?? default,
            };
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return RegionConfiguration.Default;
        }
        catch (JsonException ex)
        {
            // An unreadable row would otherwise break every location list and group tag.
            LogConfigurationUnreadable(_logger, ex);
            return RegionConfiguration.Default;
        }
    }

    /// <summary>Saves the configuration, replacing the previous one.</summary>
    public async Task UpsertAsync(RegionConfiguration configuration, CancellationToken ct = default)
    {
        var entity = new TableEntity(PartitionKey, RowKey)
        {
            ["RegionsJson"] = JsonSerializer.Serialize(configuration.Regions, JsonOptions),
            ["CountryOverridesJson"] = JsonSerializer.Serialize(configuration.CountryOverrides, JsonOptions),
            ["LastModifiedAt"] = DateTimeOffset.UtcNow,
        };

        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        LogConfigurationSaved(_logger, configuration.Regions.Count, configuration.CountryOverrides.Count);
    }

    private static T? Deserialize<T>(string? json) => json is null ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored region configuration could not be read; using the built-in default.")]
    private static partial void LogConfigurationUnreadable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Region configuration saved: {RegionCount} regions, {OverrideCount} country overrides.")]
    private static partial void LogConfigurationSaved(ILogger logger, int regionCount, int overrideCount);
}
