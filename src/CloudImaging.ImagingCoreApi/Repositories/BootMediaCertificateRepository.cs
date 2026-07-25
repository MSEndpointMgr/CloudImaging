using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Persists boot-media certificate metadata in Azure Table Storage.
///
/// Partition scheme: PartitionKey = "cert", RowKey = thumbprint.
/// Only one row has <c>IsActive = true</c> at any time.
///
/// <b>Atomic rotation</b> is implemented via a Table Storage batch transaction:
///   1. Update the old active row → IsActive = false.
///   2. Insert the new row → IsActive = true.
///   Both operations are submitted in a single <see cref="TableTransactionAction"/> batch.
/// </summary>
public sealed partial class BootMediaCertificateRepository
{
    private const string TableName = "BootMediaCertificate";
    private const string PartitionKey = "cert";

    private readonly TableClient _table;
    private readonly ILogger<BootMediaCertificateRepository> _logger;

    public BootMediaCertificateRepository(
        TableServiceClient tableService,
        ILogger<BootMediaCertificateRepository> logger)
    {
        _table = tableService.GetTableClient(TableName);
        _logger = logger;
    }

    /// <summary>Ensures the backing table exists.  Called once on startup.</summary>
    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    /// <summary>Returns the currently active certificate, or <c>null</c> if none exists.</summary>
    public async Task<BootMediaCertificate?> GetActiveAsync(CancellationToken ct = default)
    {
        await foreach (var entity in _table.QueryAsync<TableEntity>(
            e => e.PartitionKey == PartitionKey && e.GetBoolean("IsActive") == true,
            maxPerPage: 1,
            cancellationToken: ct))
        {
            return MapFromEntity(entity);
        }
        return null;
    }

    /// <summary>
    /// Inserts a new certificate record as the sole active cert, atomically demoting
    /// any previously active record in the same Table Storage batch transaction.
    /// </summary>
    public async Task ActivateAsync(BootMediaCertificate newCert, CancellationToken ct = default)
    {
        var batch = new List<TableTransactionAction>();

        // Demote any existing active cert in the same atomic batch
        await foreach (var existing in _table.QueryAsync<TableEntity>(
            e => e.PartitionKey == PartitionKey && e.GetBoolean("IsActive") == true,
            cancellationToken: ct))
        {
            existing["IsActive"] = false;
            batch.Add(new TableTransactionAction(TableTransactionActionType.UpdateMerge, existing));
        }

        // Insert (or replace) the new active cert
        var activeCert = new BootMediaCertificate
        {
            Thumbprint = newCert.Thumbprint,
            NotBefore = newCert.NotBefore,
            NotAfter = newCert.NotAfter,
            IsActive = true,
            KeyVaultSecretName = newCert.KeyVaultSecretName,
        };
        var newEntity = MapToEntity(activeCert);
        batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, newEntity));

        await _table.SubmitTransactionAsync(batch, ct);
        LogCertActivated(_logger, newCert.Thumbprint);
    }

    /// <summary>Returns all certificate records, ordered by <c>NotBefore</c> descending.</summary>
    public async Task<IReadOnlyList<BootMediaCertificate>> GetAllAsync(CancellationToken ct = default)
    {
        var list = new List<BootMediaCertificate>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(
            e => e.PartitionKey == PartitionKey,
            cancellationToken: ct))
        {
            list.Add(MapFromEntity(entity));
        }
        return list.OrderByDescending(c => c.NotBefore).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private static BootMediaCertificate MapFromEntity(TableEntity e) =>
        new()
        {
            Thumbprint = e.RowKey,
            NotBefore = e.GetDateTimeOffset("NotBefore") ?? DateTimeOffset.MinValue,
            NotAfter = e.GetDateTimeOffset("NotAfter") ?? DateTimeOffset.MinValue,
            IsActive = e.GetBoolean("IsActive") ?? false,
            KeyVaultSecretName = e.GetString("KeyVaultSecretName") ?? string.Empty,
        };

    private static TableEntity MapToEntity(BootMediaCertificate cert)
    {
        var e = new TableEntity(PartitionKey, cert.Thumbprint);
        e["NotBefore"] = cert.NotBefore;
        e["NotAfter"] = cert.NotAfter;
        e["IsActive"] = cert.IsActive;
        e["KeyVaultSecretName"] = cert.KeyVaultSecretName;
        return e;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot media certificate activated: thumbprint={Thumbprint}")]
    private static partial void LogCertActivated(ILogger logger, string thumbprint);
}
