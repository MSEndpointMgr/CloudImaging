using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Persists boot-media certificate metadata in Azure Table Storage.
///
/// Partition scheme: PartitionKey = "cert", RowKey = thumbprint.
/// Only one row has <c>IsActive = true</c> at any time; at most one other row has an
/// <c>AcceptedUntil</c> in the future (the certificate the last rotation replaced).
///
/// <b>Atomic rotation</b> is implemented via a Table Storage batch transaction:
///   1. Update the old active row → IsActive = false, AcceptedUntil = end of the overlap.
///   2. Insert the new row → IsActive = true.
///   Both operations are submitted in a single <see cref="TableTransactionAction"/> batch.
/// </summary>
public sealed partial class BootMediaCertificateRepository
{
    private const string TableName = "BootMediaCertificate";
    private const string PartitionKey = "cert";

    private readonly TableClient _table;
    private readonly ILogger<BootMediaCertificateRepository> _logger;

    /// <param name="tableService">Table service client used to resolve the boot media certificate table.</param>
    /// <param name="logger">Logger for this repository.</param>
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
    /// Inserts a new certificate record as the sole active cert, atomically demoting any previously
    /// active record in the same Table Storage batch transaction. The demoted certificate stays
    /// accepted for <paramref name="previousAcceptedFor"/> (zero ends it now); an older certificate
    /// still inside an earlier overlap stops being accepted, so at most one previous one is.
    /// </summary>
    public async Task ActivateAsync(BootMediaCertificate newCert, TimeSpan previousAcceptedFor = default, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new List<TableTransactionAction>();

        await foreach (var existing in _table.QueryAsync<TableEntity>(
            e => e.PartitionKey == PartitionKey,
            cancellationToken: ct))
        {
            if (string.Equals(existing.RowKey, newCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (existing.GetBoolean("IsActive") == true)
            {
                existing["IsActive"] = false;
                existing["AcceptedUntil"] = previousAcceptedFor > TimeSpan.Zero ? now.Add(previousAcceptedFor) : now;
                batch.Add(new TableTransactionAction(TableTransactionActionType.UpdateMerge, existing));
            }
            else if (existing.GetDateTimeOffset("AcceptedUntil") > now)
            {
                existing["AcceptedUntil"] = now;
                batch.Add(new TableTransactionAction(TableTransactionActionType.UpdateMerge, existing));
            }
        }

        // Insert (or replace) the new active cert
        var activeCert = new BootMediaCertificate
        {
            Thumbprint = newCert.Thumbprint,
            NotBefore = newCert.NotBefore,
            NotAfter = newCert.NotAfter,
            IsActive = true,
            KeyVaultSecretName = newCert.KeyVaultSecretName,
            CertificateDer = newCert.CertificateDer,
        };
        var newEntity = MapToEntity(activeCert);
        batch.Add(new TableTransactionAction(TableTransactionActionType.UpsertReplace, newEntity));

        await _table.SubmitTransactionAsync(batch, ct);
        LogCertActivated(_logger, newCert.Thumbprint);
    }

    /// <summary>The active certificate plus a previous one still inside its overlap.</summary>
    public async Task<IReadOnlyList<BootMediaCertificate>> GetAcceptedAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var list = new List<BootMediaCertificate>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(
            e => e.PartitionKey == PartitionKey,
            cancellationToken: ct))
        {
            var cert = MapFromEntity(entity);
            if (cert.IsActive || cert.AcceptedUntil > now)
            {
                list.Add(cert);
            }
        }
        return list.OrderByDescending(c => c.IsActive).ToList();
    }

    /// <summary>Ends the overlap of any previous certificate now. Returns how many were still accepted.</summary>
    public async Task<int> StopAcceptingPreviousAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var batch = new List<TableTransactionAction>();
        await foreach (var entity in _table.QueryAsync<TableEntity>(
            e => e.PartitionKey == PartitionKey,
            cancellationToken: ct))
        {
            if (entity.GetBoolean("IsActive") != true && entity.GetDateTimeOffset("AcceptedUntil") > now)
            {
                entity["AcceptedUntil"] = now;
                batch.Add(new TableTransactionAction(TableTransactionActionType.UpdateMerge, entity));
            }
        }
        if (batch.Count > 0)
        {
            await _table.SubmitTransactionAsync(batch, ct);
            LogPreviousStopped(_logger, batch.Count);
        }
        return batch.Count;
    }

    /// <summary>Stores the public certificate for a record created before it was kept.</summary>
    public async Task SetCertificateDerAsync(string thumbprint, string certificateDer, CancellationToken ct = default)
    {
        var entity = new TableEntity(PartitionKey, thumbprint) { ["CertificateDer"] = certificateDer };
        await _table.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge, ct);
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
            AcceptedUntil = e.GetDateTimeOffset("AcceptedUntil"),
            CertificateDer = e.GetString("CertificateDer"),
        };

    private static TableEntity MapToEntity(BootMediaCertificate cert)
    {
        var e = new TableEntity(PartitionKey, cert.Thumbprint);
        e["NotBefore"] = cert.NotBefore;
        e["NotAfter"] = cert.NotAfter;
        e["IsActive"] = cert.IsActive;
        e["KeyVaultSecretName"] = cert.KeyVaultSecretName;
        if (cert.AcceptedUntil is { } acceptedUntil)
        {
            e["AcceptedUntil"] = acceptedUntil;
        }
        if (!string.IsNullOrEmpty(cert.CertificateDer))
        {
            e["CertificateDer"] = cert.CertificateDer;
        }
        return e;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot media certificate activated: thumbprint={Thumbprint}")]
    private static partial void LogCertActivated(ILogger logger, string thumbprint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped accepting {Count} previous boot media certificate(s).")]
    private static partial void LogPreviousStopped(ILogger logger, int count);
}
