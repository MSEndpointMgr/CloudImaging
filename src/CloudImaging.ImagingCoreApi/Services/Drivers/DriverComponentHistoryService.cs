using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// A component's version history and its roll back (FR-089 to FR-093): which versions it has had,
/// whether each is stored or can still be downloaded, and holding one earlier version while every
/// other component keeps updating.
/// </summary>
public sealed class DriverComponentHistoryService
{
    /// <summary>How long one availability check waits for the vendor.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverStorageService _storage;
    private readonly DriverModelService _modelService;
    private readonly DriverPolicyService _policy;
    private readonly TimeProvider _time;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="storage">Downloads and availability checks.</param>
    /// <param name="modelService">Component rows.</param>
    /// <param name="policy">Marks pairs dirty.</param>
    /// <param name="time">Clock.</param>
    public DriverComponentHistoryService(DriverStore store, DriverModelRepository models, DriverStorageService storage, DriverModelService modelService, DriverPolicyService policy, TimeProvider time)
    {
        _store = store;
        _models = models;
        _storage = storage;
        _modelService = modelService;
        _policy = policy;
        _time = time;
    }

    /// <summary>
    /// The versions a component has had, newest first. With <paramref name="check"/>, every version
    /// that is not stored is first checked at the vendor, in parallel.
    /// </summary>
    public async Task<DriverResult> VersionsAsync(Guid modelId, WindowsRelease release, string lineageKey, bool check, CancellationToken ct = default)
    {
        var model = (await _models.GetAsync(modelId, ct))?.Value;
        var pair = DriverTables.Pair(modelId, release);
        if (model is null || !model.Releases.Contains(release) || await _store.Components.GetAsync(pair, lineageKey, ct) is null)
        {
            return DriverResult.NotFound("Component not found.");
        }

        var rows = (await _store.ComponentVersions.ListPartitionAsync(pair, ct)).Where(r => r.Value.LineageKey == lineageKey).OrderByDescending(r => r.Value.From).ToList();
        var stored = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            stored[row.RowKey] = row.Value.Origin == DriverComponentOrigin.Custom
                || (await _store.Files.GetAsync(model.Vendor.ToString(), DriverSyncService.FileKey(row.Value.ComponentId, row.Value.Version), ct))?.Value.Status == DriverFileStatus.Verified;
        }

        if (check)
        {
            var now = _time.GetUtcNow();
            var probes = rows.Where(r => !stored[r.RowKey] && r.Value.Url is not null)
                .Select(async r => (Row: r, Available: await _storage.ProbeAsync(r.Value.Url!, ProbeTimeout, ct)))
                .ToList();
            foreach (var (row, available) in await Task.WhenAll(probes))
            {
                var updated = row.Value with { Available = available, AvailabilityCheckedAt = now };
                await _store.ComponentVersions.UpsertAsync(pair, row.RowKey, updated, ct);
                rows[rows.IndexOf(row)] = row with { Value = updated };
            }
        }

        return DriverResult.Ok(rows.Select(r => new DriverComponentVersion
        {
            ComponentId = r.Value.ComponentId,
            Version = r.Value.Version,
            FirstSet = r.Value.FirstSet,
            LastSet = r.Value.LastSet,
            From = r.Value.From,
            To = r.Value.To,
            ChangedBy = r.Value.ChangedBy,
            Origin = r.Value.Origin,
            Status = stored[r.RowKey] ? DriverVersionStatus.Stored
                : r.Value.Available == false || r.Value.Url is null ? DriverVersionStatus.Unavailable
                : DriverVersionStatus.DownloadsOnRollBack,
            SizeBytes = r.Value.Size,
        }).ToList());
    }

    /// <summary>
    /// Holds one earlier version. A version that is not stored is downloaded again and verified
    /// first; when that fails nothing is written and the reason is returned with 409.
    /// </summary>
    public async Task<DriverResult> SetRollBackAsync(Guid modelId, WindowsRelease release, string lineageKey, DriverComponentRollBackRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var model = (await _models.GetAsync(modelId, ct))?.Value;
        var pair = DriverTables.Pair(modelId, release);
        var component = model is not null && model.Releases.Contains(release) ? (await _store.Components.GetAsync(pair, lineageKey, ct))?.Value : null;
        if (model is null || component is null)
        {
            return DriverResult.NotFound("Component not found.");
        }

        var held = (await _store.ComponentVersions.ListPartitionAsync(pair, ct))
            .Select(r => r.Value)
            .Where(v => v.LineageKey == lineageKey && v.Version == request.Version && v.Origin != DriverComponentOrigin.Custom)
            .MaxBy(v => v.From);
        if (held is null || request.Version == component.Version || !Enum.IsDefined(request.Until))
        {
            return DriverResult.BadRequest("Choose an earlier version from the list.");
        }

        var vendor = DriverSyncService.VendorName(model.Vendor);
        var fileKey = DriverSyncService.FileKey(held.ComponentId, held.Version);
        var file = (await _store.Files.GetAsync(model.Vendor.ToString(), fileKey, ct))?.Value;
        if (file?.Status != DriverFileStatus.Verified)
        {
            if (held.Url is null || string.IsNullOrEmpty(held.Sha256))
            {
                return DriverResult.Conflict($"{vendor} no longer offers version {held.Version}.");
            }

            var blob = DriverSyncService.HeldBlob(model.Vendor, held);
            var outcome = await _storage.DownloadAsync(held.Url, blob, held.Sha256, held.Size > 0 ? held.Size : null, ct);
            if (!outcome.Succeeded)
            {
                return DriverResult.Conflict($"Version {held.Version} could not be downloaded again from {vendor}: {outcome.Reason}");
            }

            await _store.Files.UpsertAsync(model.Vendor.ToString(), fileKey, new DriverFileRecord
            {
                BlobName = blob,
                Size = outcome.Size,
                Sha256 = outcome.Sha256 ?? held.Sha256,
                Status = DriverFileStatus.Verified,
                SourceUrl = held.Url,
                FileName = held.FileName,
                ComponentName = component.Name,
                VerifiedAt = _time.GetUtcNow(),
                AttemptedAt = _time.GetUtcNow(),
            }, ct);
        }

        var now = _time.GetUtcNow();
        await _store.RollBacks.UpsertAsync(pair, lineageKey, new DriverRollBackRecord
        {
            Version = held.Version,
            Until = request.Until,
            SkippedVersion = component.Version,
            ChangedAt = now,
            ChangedBy = upn,
        }, ct);
        var until = request.Until == DriverRollBackUntil.NewerVersion ? $"until {vendor} publishes a version newer than {component.Version}" : "until the roll back is undone";
        await _store.LogAsync(now, upn, request.Actor.ObjectId, "Component", $"{model.DisplayName}: {component.Name}", $"Rolled back to {held.Version} {until}, {DriverModelService.ReleaseLabel(release)}", ct);
        await _policy.MarkDirtyAsync(model, release, now, ct);
        return await RowAsync(model, release, lineageKey, ct);
    }

    /// <summary>Ends a roll back; 404 when the component is not rolled back.</summary>
    public async Task<DriverResult> ClearRollBackAsync(Guid modelId, WindowsRelease release, string lineageKey, string? changedBy, CancellationToken ct = default)
    {
        if (changedBy is not { Length: > 0 })
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var model = (await _models.GetAsync(modelId, ct))?.Value;
        var pair = DriverTables.Pair(modelId, release);
        if (model is null || !model.Releases.Contains(release) || await _store.RollBacks.GetAsync(pair, lineageKey, ct) is not { } rollBack)
        {
            return DriverResult.NotFound("This component is not rolled back.");
        }

        var now = _time.GetUtcNow();
        await _store.RollBacks.DeleteAsync(pair, lineageKey, ct: ct);
        var name = (await _store.Components.GetAsync(pair, lineageKey, ct))?.Value.Name ?? lineageKey;
        await _store.LogAsync(now, changedBy, null, "Component", $"{model.DisplayName}: {name}", $"Roll back to {rollBack.Value.Version} undone, {DriverModelService.ReleaseLabel(release)}", ct);
        await _policy.MarkDirtyAsync(model, release, now, ct);
        return await RowAsync(model, release, lineageKey, ct);
    }

    private async Task<DriverResult> RowAsync(DriverModelRecord model, WindowsRelease release, string lineageKey, CancellationToken ct) =>
        await _modelService.GetComponentRowAsync(model, release, lineageKey, ct) is { } row ? DriverResult.Ok(row) : DriverResult.NotFound("Component not found.");
}
