using Azure.Data.Tables;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Persists and retrieves the global <see cref="PortalConfiguration"/> record from
/// Azure Table Storage.  Only one configuration row exists (PartitionKey="config",
/// RowKey="default").  A sensible default is returned when no row exists yet.
/// </summary>
public sealed partial class PortalConfigurationRepository
{
    private const string TableName = "PortalConfiguration";
    private const string PartitionKey = "config";
    private const string RowKey = "default";

    private readonly TableClient _table;
    private readonly ILogger<PortalConfigurationRepository> _logger;

    public PortalConfigurationRepository(
        TableServiceClient tableService,
        ILogger<PortalConfigurationRepository> logger)
    {
        _table = tableService.GetTableClient(TableName);
        _logger = logger;
    }

    /// <summary>Ensures the backing table exists.  Called once on startup.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>
    /// Returns the current portal configuration, or a safe default if none has been
    /// persisted yet.
    /// </summary>
    public async Task<PortalConfiguration> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(PartitionKey, RowKey, cancellationToken: ct);
            return MapFromEntity(response.Value);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            LogNoConfigFound(_logger);
            return PortalConfigurationDefaults();
        }
    }

    /// <summary>Upserts the portal configuration.</summary>
    public async Task UpsertAsync(PortalConfiguration config, CancellationToken ct = default)
    {
        var entity = new TableEntity(PartitionKey, RowKey)
        {
            [nameof(PortalConfiguration.DevicePreFlightAuthorizationEnabled)] = config.DevicePreFlightAuthorizationEnabled,
            [nameof(PortalConfiguration.SasTokenUrlExpiryMinutes)] = config.SasTokenUrlExpiryMinutes,
            [nameof(PortalConfiguration.BootImageSasExpiryMinutes)] = config.BootImageSasExpiryMinutes,
            [nameof(PortalConfiguration.CertValidityPeriodDays)] = config.CertValidityPeriodDays,
            [nameof(PortalConfiguration.ClockSkewToleranceSeconds)] = config.ClockSkewToleranceSeconds,
            [nameof(PortalConfiguration.SessionHistoryRetentionDays)] = config.SessionHistoryRetentionDays,
        };

        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
        LogConfigUpserted(_logger);
    }

    // ------------------------------------------------------------------ helpers

    private static PortalConfiguration MapFromEntity(TableEntity e) =>
        new()
        {
            DevicePreFlightAuthorizationEnabled = e.GetBoolean(nameof(PortalConfiguration.DevicePreFlightAuthorizationEnabled)) ?? false,
            SasTokenUrlExpiryMinutes = e.GetInt32(nameof(PortalConfiguration.SasTokenUrlExpiryMinutes)) ?? 240,
            BootImageSasExpiryMinutes = e.GetInt32(nameof(PortalConfiguration.BootImageSasExpiryMinutes)) ?? 120,
            CertValidityPeriodDays = e.GetInt32(nameof(PortalConfiguration.CertValidityPeriodDays)) ?? 365,
            ClockSkewToleranceSeconds = e.GetInt32(nameof(PortalConfiguration.ClockSkewToleranceSeconds)) ?? 30,
            SessionHistoryRetentionDays = e.GetInt32(nameof(PortalConfiguration.SessionHistoryRetentionDays)) ?? 90,
        };

    private static PortalConfiguration PortalConfigurationDefaults() =>
        new()
        {
            DevicePreFlightAuthorizationEnabled = false,
            SasTokenUrlExpiryMinutes = 240,
            BootImageSasExpiryMinutes = 120,
            CertValidityPeriodDays = 365,
            ClockSkewToleranceSeconds = 30,
            SessionHistoryRetentionDays = 90,
        };

    [LoggerMessage(Level = LogLevel.Information, Message = "No PortalConfiguration row found — returning defaults.")]
    private static partial void LogNoConfigFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "PortalConfiguration upserted.")]
    private static partial void LogConfigUpserted(ILogger logger);
}
