using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>The BIOS part of a run: one <c>ResolveFirmware</c> message per HP model.</summary>
public sealed partial class DriverSyncService
{
    // Reads HP's BIOS catalog of every platform of the model, merges it and queues the files
    // devices may be given. A draft is read but downloads nothing.
    private async Task<StepResult> ResolveFirmwareAsync(DriverWorkMessage message, CancellationToken ct)
    {
        if (message is not { ModelId: { } modelId }
            || (await _models.GetAsync(modelId, ct))?.Value is not { } model
            || !DriverFirmwareService.Supports(model))
        {
            return NoWork;
        }

        var now = _time.GetUtcNow();
        var run = message.RunId is { } runId ? (await _runs.GetAsync(runId, ct))?.Value : null;
        var settings = (await _store.GetSyncSettingsAsync(ct)).Value;
        var items = new List<DriverSyncRunItem>();
        var downloads = new List<DriverWorkMessage>();
        foreach (var platform in model.MatchKeys.Select(DriverTables.NormalizeKey).Distinct(StringComparer.Ordinal))
        {
            var known = (await _store.Firmware.GetAsync(modelId.ToString("D"), platform, ct))?.Value;
            var read = await _catalogs.ReadAsync(DriverVendor.Hp, HpBiosCatalog.CatalogUrl(platform), null, null, ct);
            IReadOnlyList<DriverFirmwareRelease>? releases = null;
            var failure = read.Failure;
            if (read.Content is { } content)
            {
                await using (content)
                {
                    try
                    {
                        releases = HpBiosCatalog.Parse(content, platform);
                    }
                    catch (System.Xml.XmlException)
                    {
                        failure = $"The BIOS catalog of {platform} could not be read.";
                    }
                }
            }

            // HP publishes no BIOS catalog for some platform IDs; that is only a failure once one was read.
            if (releases is not { Count: > 0 })
            {
                if (known is not null)
                {
                    items.Add(FirmwareItem(model, platform, null, null, DriverSyncItemOutcome.Failed) with { Reason = failure ?? $"HP lists no BIOS for {platform}." });
                }

                continue;
            }

            DriverFirmwareMergeChange change = DriverFirmwareMergeChange.None;
            var rollBackEnded = false;
            var (before, after) = await DriverFirmwareService.UpdateAsync(_store, modelId, platform, old =>
            {
                (var next, change, rollBackEnded) = DriverFirmwareService.Merge(old, platform, releases, settings, now);
                var unchanged = old is not null && change == DriverFirmwareMergeChange.None && old.Latest == next.Latest
                    && old.PreProductionVersion == next.PreProductionVersion && old.Releases.SequenceEqual(next.Releases);
                return unchanged ? null : next;
            }, ct);
            if (after is null)
            {
                continue;
            }

            if (rollBackEnded)
            {
                await _store.LogAsync(now, SyncChange, null, DriverFirmwareService.LogArea, $"{model.DisplayName}: {DriverFirmwareService.ComponentName} ({platform})", $"Roll back ended: HP published {after.Latest.Version}", ct);
            }

            if (change is DriverFirmwareMergeChange.New or DriverFirmwareMergeChange.Released)
            {
                var onboarding = run?.Trigger == DriverSyncTrigger.Onboarding;
                await DriverFirmwareService.RecordVersionAsync(_store, modelId, after, onboarding ? DriverVersionChange.Onboarding : DriverVersionChange.Sync, onboarding ? run?.StartedBy : null, now, ct);
            }

            if (change is not (DriverFirmwareMergeChange.None or DriverFirmwareMergeChange.New))
            {
                items.Add(FirmwareItem(model, platform, before?.Latest.Version, after.Latest.Version, DriverSyncItemOutcome.Updated));
            }

            if (model.State == DriverModelState.Active)
            {
                downloads.AddRange(await DriverFirmwareService.EnsureFilesAsync(_store, modelId, after, run?.StartedAt, ct));
            }
        }

        var recorded = new List<DriverSyncRunItem>();
        if (message.RunId is { } itemsRun)
        {
            foreach (var item in items)
            {
                recorded.Add(await _runs.AddItemAsync(itemsRun, now, item, ct));
            }
        }

        return new StepResult(downloads, Items: recorded);
    }

    private static DriverSyncRunItem FirmwareItem(DriverModelRecord model, string platform, string? oldVersion, string? newVersion, DriverSyncItemOutcome outcome) => new()
    {
        ModelId = model.ModelId,
        ModelName = model.DisplayName,
        LineageKey = DriverFirmwareService.ComponentId(platform),
        ComponentName = $"{DriverFirmwareService.ComponentName} ({platform})",
        OldVersion = oldVersion,
        NewVersion = newVersion,
        Outcome = outcome,
        Vendor = model.Vendor,
        Category = DriverFirmwareService.Category,
    };
}
