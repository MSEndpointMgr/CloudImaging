using System.Globalization;
using System.Text;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>How a setup profile operation ended, mapped to an HTTP status by the functions.</summary>
public enum SetupProfileOutcome
{
    /// <summary>200.</summary>
    Ok,

    /// <summary>201.</summary>
    Created,

    /// <summary>204.</summary>
    NoContent,

    /// <summary>400.</summary>
    BadRequest,

    /// <summary>404.</summary>
    NotFound,

    /// <summary>409.</summary>
    Conflict,

    /// <summary>413.</summary>
    PayloadTooLarge,
}

/// <summary>Result of a setup profile operation.</summary>
/// <param name="Outcome">Outcome.</param>
/// <param name="Value">Response body on success.</param>
/// <param name="Detail">Ready-to-show problem sentence on failure.</param>
/// <param name="Errors">Answer file problems, when the answer file was refused.</param>
public sealed record SetupProfileResult(SetupProfileOutcome Outcome, object? Value = null, string? Detail = null, IReadOnlyList<AnswerFileProblem>? Errors = null)
{
    internal static SetupProfileResult NotFound() => new(SetupProfileOutcome.NotFound, Detail: SetupProfileService.NotFoundMessage);

    internal static SetupProfileResult BadRequest(string detail, IReadOnlyList<AnswerFileProblem>? errors = null) => new(SetupProfileOutcome.BadRequest, Detail: detail, Errors: errors);

    internal static SetupProfileResult Conflict(string detail) => new(SetupProfileOutcome.Conflict, Detail: detail);
}

/// <summary>What happened to setup profiles when a location was deleted.</summary>
/// <param name="RefusedDetail">Set when the location is a profile's only target; nothing may be deleted.</param>
/// <param name="AffectedProfileId">Profile that lost the location, if any.</param>
public sealed record SetupProfileLocationRemoval(string? RefusedDetail, Guid? AffectedProfileId);

/// <summary>
/// Setup profile rules and storage: create, save, restore, delete, reads and pruning. Profiles are
/// stored as numbered immutable versions; targeting is not versioned. Save steps run in an order
/// that leaves a consistent state when any of them fails.
/// </summary>
public sealed partial class SetupProfileService
{
    /// <summary>Message for an unknown or deleted profile.</summary>
    public const string NotFoundMessage = "This setup profile no longer exists.";

    private const string MissingActorMessage = "The acting user is missing.";

    /// <summary>How long an uploaded text no version references is kept, so a save can still use it.</summary>
    public static readonly TimeSpan ContentGracePeriod = TimeSpan.FromHours(24);

    /// <summary>Shortest time between two content cleanups.</summary>
    public static readonly TimeSpan ContentSweepInterval = TimeSpan.FromDays(1);

    private readonly SetupProfileRepository _profiles;
    private readonly SetupProfileTargetRepository _targets;
    private readonly SetupProfileVersionRepository _versions;
    private readonly SetupProfileContentRepository _content;
    private readonly SetupProfileHistoryRepository _history;
    private readonly LocationRepository _locations;
    private readonly DeviceSessionRepository _sessions;
    private readonly PortalConfigurationRepository _config;
    private readonly TimeProvider _time;
    private readonly ILogger<SetupProfileService> _logger;
    private DateTimeOffset? _lastContentSweep;

    /// <param name="profiles">Profile rows.</param>
    /// <param name="targets">Targeting index.</param>
    /// <param name="versions">Version rows and documents.</param>
    /// <param name="content">Uploaded script and answer file texts.</param>
    /// <param name="history">Profile history.</param>
    /// <param name="locations">Location catalog.</param>
    /// <param name="sessions">Device sessions, for pinned versions.</param>
    /// <param name="config">Portal configuration, for version retention.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger for audit events.</param>
    public SetupProfileService(
        SetupProfileRepository profiles,
        SetupProfileTargetRepository targets,
        SetupProfileVersionRepository versions,
        SetupProfileContentRepository content,
        SetupProfileHistoryRepository history,
        LocationRepository locations,
        DeviceSessionRepository sessions,
        PortalConfigurationRepository config,
        TimeProvider time,
        ILogger<SetupProfileService> logger)
    {
        _profiles = profiles;
        _targets = targets;
        _versions = versions;
        _content = content;
        _history = history;
        _locations = locations;
        _sessions = sessions;
        _config = config;
        _time = time;
        _logger = logger;
    }

    // ── Reads ───────────────────────────────────────────────────────────────

    /// <summary>Lists profiles that are not deleted, with their targeting.</summary>
    public async Task<List<SetupProfileSummary>> ListAsync(CancellationToken ct = default)
    {
        var owners = await _targets.ListAllAsync(ct);
        var profiles = await _profiles.ListActiveAsync(ct);
        return [.. profiles.Select(p => ToSummary(p, TargetingFrom(owners, p.ProfileId)))];
    }

    /// <summary>Returns a profile with its current content, or null when it is unknown or deleted.</summary>
    public async Task<SetupProfileDetail?> GetDetailAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await _profiles.GetAsync(profileId, ct);
        if (profile is null || profile.IsDeleted)
        {
            return null;
        }
        return await BuildDetailAsync(profile, ct);
    }

    /// <summary>Lists every retained version, newest first, with running deployment counts.</summary>
    public async Task<List<SetupProfileVersionSummary>?> ListVersionsAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await _profiles.GetAsync(profileId, ct);
        if (profile is null || profile.IsDeleted)
        {
            return null;
        }
        var inUse = await _sessions.CountActiveBySetupProfileVersionAsync(profileId, ct);
        var rows = await _versions.ListAsync(profileId, ct);
        return [.. rows.Select(row => ToVersionSummary(row, profile.CurrentVersion, inUse))];
    }

    /// <summary>Returns one retained version with its content, or null.</summary>
    public async Task<SetupProfileVersionDetail?> GetVersionAsync(Guid profileId, int version, CancellationToken ct = default)
    {
        var profile = await _profiles.GetAsync(profileId, ct);
        if (profile is null || profile.IsDeleted || await _versions.GetAsync(profileId, version, ct) is not { } row)
        {
            return null;
        }
        if (await _versions.DownloadAsync(row.BlobName, ct) is not { } content)
        {
            return null;
        }
        var inUse = await _sessions.CountActiveBySetupProfileVersionAsync(profileId, ct);
        var summary = ToVersionSummary(row, profile.CurrentVersion, inUse);
        return new SetupProfileVersionDetail
        {
            Version = summary.Version,
            CreatedAt = summary.CreatedAt,
            CreatedBy = summary.CreatedBy,
            ChangeSummary = summary.ChangeSummary,
            RestoredFrom = summary.RestoredFrom,
            DuplicatedFrom = summary.DuplicatedFrom,
            IsCurrent = summary.IsCurrent,
            InUseCount = summary.InUseCount,
            Content = content,
        };
    }

    /// <summary>Returns the profile-level history (targeting, renames, creation, deletion), newest first.</summary>
    public async Task<List<SetupProfileHistoryEntry>?> ListHistoryAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await _profiles.GetAsync(profileId, ct);
        if (profile is null || profile.IsDeleted)
        {
            return null;
        }
        var rows = await _history.ListAsync(profileId, ct);
        return [.. rows
            .Where(r => r.Kind is SetupProfileHistoryKind.Created or SetupProfileHistoryKind.Renamed or SetupProfileHistoryKind.TargetingChanged
                or SetupProfileHistoryKind.LocationDeleted or SetupProfileHistoryKind.Deleted)
            .Select(r => new SetupProfileHistoryEntry { Kind = r.Kind, At = r.At, By = r.By, Change = r.Change })];
    }

    // ── Create ──────────────────────────────────────────────────────────────

    /// <summary>Creates a profile with version 1.</summary>
    public async Task<SetupProfileResult> CreateAsync(CreateSetupProfileRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Actor?.Upn))
        {
            return SetupProfileResult.BadRequest(MissingActorMessage);
        }
        var actor = request.Actor!;
        var now = _time.GetUtcNow();
        var profileId = Guid.NewGuid();
        var name = request.Name?.Trim() ?? string.Empty;

        var locations = await LoadLocationsAsync(ct);
        var (invalid, facts) = await ValidateAsync(name, request.Targeting, request.Content, null, locations, profileId, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        SetupProfileDuplicatedFrom? duplicatedFrom = null;
        if (request.DuplicatedFrom is { } source)
        {
            var sourceProfile = await _profiles.GetAsync(source.ProfileId, ct);
            if (sourceProfile is null || await _versions.GetAsync(source.ProfileId, source.Version, ct) is null)
            {
                return SetupProfileResult.BadRequest("The profile you duplicated no longer has that version. Reload the page and duplicate it again.");
            }
            duplicatedFrom = new SetupProfileDuplicatedFrom { ProfileId = source.ProfileId, ProfileName = sourceProfile.Name, Version = source.Version };
        }

        var (adds, deletes) = TargetChanges([], request.Targeting);
        if (!await _profiles.TryReserveNameAsync(name, profileId, now, ct))
        {
            return DuplicateName(name);
        }
        var targetResult = await ClaimTargetsAsync(profileId, adds, deletes, ct);
        if (!targetResult.Succeeded)
        {
            await _profiles.ReleaseNameAsync(name, profileId, CancellationToken.None);
            return await TargetConflictAsync(targetResult, locations, ct);
        }

        var content = Normalize(request.Content, null, profileId, 1, now, actor.Upn, facts);
        var record = new SetupProfileRecord
        {
            ProfileId = profileId,
            Name = name,
            CurrentVersion = 1,
            FailureHandling = content.FailureHandling,
            RegistryValueCount = content.RegistryValues.Count,
            ScriptCount = content.Scripts.Count,
            HasAnswerFile = content.AnswerFile is not null,
            CreatedAt = now,
            CreatedBy = actor.Upn,
            LastModifiedAt = now,
            LastModifiedBy = actor.Upn,
        };
        SetupProfileVersionRecord? row = null;
        try
        {
            row = await WriteVersionAsync(content, null, now, actor.Upn, restoredFrom: null, duplicatedFrom, ct);
            await _profiles.AddAsync(record, ct);
        }
        catch
        {
            // Otherwise the locations stay claimed by a profile that was never stored.
            await _targets.RevertChangesAsync(profileId, adds, deletes, CancellationToken.None);
            await _profiles.ReleaseNameAsync(name, profileId, CancellationToken.None);
            if (row is not null)
            {
                await _versions.DeleteAsync(row, CancellationToken.None);
            }
            throw;
        }

        await AddHistoryAsync(profileId, SetupProfileHistoryKind.Created, actor, now, $"Created, targeting {DescribeTargeting(request.Targeting, locations)}", ct: ct);
        if (duplicatedFrom is not null)
        {
            await AddHistoryAsync(profileId, SetupProfileHistoryKind.Duplicated, actor, now, $"Duplicated from {duplicatedFrom.ProfileName}, version {duplicatedFrom.Version}", version: 1, ct: ct);
        }
        await AddHistoryAsync(profileId, SetupProfileHistoryKind.VersionSaved, actor, now, "Saved version 1", version: 1, ct: ct);
        LogAudit(_logger, "created", profileId, actor.Upn, actor.ObjectId, 1);

        var created = await _profiles.GetAsync(profileId, ct);
        return new SetupProfileResult(SetupProfileOutcome.Created, await BuildDetailAsync(created!, ct));
    }

    // ── Save and restore ────────────────────────────────────────────────────

    /// <summary>Saves name, targeting and (when present) new content as the next version.</summary>
    public async Task<SetupProfileResult> SaveAsync(Guid profileId, SaveSetupProfileRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Actor?.Upn))
        {
            return SetupProfileResult.BadRequest(MissingActorMessage);
        }
        return await SaveCoreAsync(profileId, request.BaseVersion, request.Name, request.Targeting, request.Content, restoredFrom: null, request.Actor!, ct);
    }

    /// <summary>
    /// The locations a profile's location scripts can run at now: its targeted locations, or every
    /// existing location for an All devices profile. A location removed from the targeting or
    /// deleted stays in older version documents but is no longer in effect.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> ScriptLocationsInEffectAsync(Guid profileId, CancellationToken ct = default) =>
        SetupProfileValidator.AllowedScriptLocations(TargetingFrom(await _targets.ListAllAsync(ct), profileId), (await LoadLocationsAsync(ct)).Keys.ToHashSet());

    /// <summary>Creates a new current version with the content of an earlier version. Name and targeting do not change.</summary>
    public async Task<SetupProfileResult> RestoreAsync(Guid profileId, RestoreSetupProfileVersionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Actor?.Upn))
        {
            return SetupProfileResult.BadRequest(MissingActorMessage);
        }
        var profile = await _profiles.GetAsync(profileId, ct);
        if (profile is null || profile.IsDeleted)
        {
            return SetupProfileResult.NotFound();
        }
        if (await _versions.GetAsync(profileId, request.Version, ct) is not { } row
            || await _versions.DownloadAsync(row.BlobName, ct) is not { } content)
        {
            return SetupProfileResult.NotFound();
        }
        var targeting = TargetingFrom(await _targets.ListAllAsync(ct), profileId);

        // Scripts keep only the locations the profile still targets, as they would have in the editor.
        var allowed = SetupProfileValidator.AllowedScriptLocations(targeting, (await LoadLocationsAsync(ct)).Keys.ToHashSet());
        content = content with
        {
            Scripts = [.. content.Scripts.Select(s => s.Locations.All ? s : s with { Locations = s.Locations with { LocationIds = [.. s.Locations.LocationIds.Where(allowed.Contains)] } })],
        };
        return await SaveCoreAsync(profileId, request.BaseVersion, profile.Name, targeting, content, restoredFrom: request.Version, request.Actor!, ct);
    }

    private async Task<SetupProfileResult> SaveCoreAsync(
        Guid profileId, int baseVersion, string? requestedName, SetupProfileTargeting? targeting, SetupProfileContent? requestedContent,
        int? restoredFrom, SetupProfileActor actor, CancellationToken ct)
    {
        // 1. Load with ETag; refuse a stale base version.
        var profile = await _profiles.GetAsync(profileId, ct);
        if (profile is null || profile.IsDeleted)
        {
            return SetupProfileResult.NotFound();
        }
        if (baseVersion != profile.CurrentVersion)
        {
            return SetupProfileResult.Conflict(StaleMessage(profile));
        }

        // 2. Validate everything at once.
        var name = requestedName?.Trim() ?? string.Empty;
        var locations = await LoadLocationsAsync(ct);
        var currentRow = await _versions.GetAsync(profileId, profile.CurrentVersion, ct);
        var current = currentRow is null ? null : await _versions.DownloadAsync(currentRow.BlobName, ct);
        var (invalid, facts) = await ValidateAsync(name, targeting, requestedContent, current, locations, profileId, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        // 3. Decide whether the content needs a new version.
        var now = _time.GetUtcNow();
        SetupProfileContent? next = null;
        if (requestedContent is not null)
        {
            var candidate = Normalize(requestedContent, current, profileId, profile.CurrentVersion + 1, now, actor.Upn, facts);
            if (restoredFrom is not null || current is null || !SameContent(current, candidate))
            {
                next = candidate;
            }
        }

        // 4. Name claim, then targeting as one transaction.
        var renamed = !string.Equals(name, profile.Name.Trim(), StringComparison.OrdinalIgnoreCase);
        if (renamed && !await _profiles.TryReserveNameAsync(name, profileId, now, ct))
        {
            return DuplicateName(name);
        }
        async Task UndoAsync()
        {
            if (renamed)
            {
                await _profiles.ReleaseNameAsync(name, profileId, CancellationToken.None);
            }
        }
        var owners = await _targets.ListAllAsync(ct);
        var before = TargetingFrom(owners, profileId);
        var (adds, deletes) = TargetChanges(Keys(before), targeting!);
        var targetResult = await ClaimTargetsAsync(profileId, adds, deletes, ct);
        if (!targetResult.Succeeded)
        {
            await UndoAsync();
            return await TargetConflictAsync(targetResult, locations, ct);
        }

        // 5. Version document and row.
        SetupProfileVersionRecord? written = null;
        try
        {
            if (next is not null)
            {
                written = await WriteVersionAsync(next, current, now, actor.Upn, restoredFrom, duplicatedFrom: null, ct);
            }
        }
        catch
        {
            await _targets.RevertChangesAsync(profileId, adds, deletes, CancellationToken.None);
            await UndoAsync();
            throw;
        }

        // 6. Profile row, guarded by the ETag from step 1.
        var nameChanged = !string.Equals(name, profile.Name, StringComparison.Ordinal);
        var targetingChanged = adds.Count > 0 || deletes.Count > 0;
        if (next is null && !nameChanged && !targetingChanged)
        {
            return new SetupProfileResult(SetupProfileOutcome.Ok, await BuildDetailAsync(profile, ct));
        }
        var updated = profile with
        {
            Name = name,
            CurrentVersion = next?.Version ?? profile.CurrentVersion,
            FailureHandling = next?.FailureHandling ?? profile.FailureHandling,
            RegistryValueCount = next?.RegistryValues.Count ?? profile.RegistryValueCount,
            ScriptCount = next?.Scripts.Count ?? profile.ScriptCount,
            HasAnswerFile = next is null ? profile.HasAnswerFile : next.AnswerFile is not null,
            LastModifiedAt = now,
            LastModifiedBy = actor.Upn,
        };
        bool replaced;
        try
        {
            replaced = await _profiles.ReplaceAsync(updated, ct);
        }
        catch
        {
            await _targets.RevertChangesAsync(profileId, adds, deletes, CancellationToken.None);
            await UndoAsync();
            if (written is not null)
            {
                await _versions.DeleteAsync(written, CancellationToken.None);
            }
            throw;
        }
        if (!replaced)
        {
            // The orphaned version, if any, is removed by the next prune.
            await _targets.RevertChangesAsync(profileId, adds, deletes, ct);
            await UndoAsync();
            var latest = await _profiles.GetAsync(profileId, ct);
            return latest is null || latest.IsDeleted ? SetupProfileResult.NotFound() : SetupProfileResult.Conflict(StaleMessage(latest));
        }
        if (renamed)
        {
            await _profiles.ReleaseNameAsync(profile.Name, profileId, CancellationToken.None);
        }

        // 7. History and audit.
        if (nameChanged)
        {
            await AddHistoryAsync(profileId, SetupProfileHistoryKind.Renamed, actor, now, $"Renamed from {profile.Name} to {name}", ct: ct);
        }
        if (targetingChanged)
        {
            await AddHistoryAsync(profileId, SetupProfileHistoryKind.TargetingChanged, actor, now, DescribeTargetingChange(before, targeting!, locations), LocationIds(adds), LocationIds(deletes), ct: ct);
        }
        if (next is not null)
        {
            var (kind, change) = restoredFrom is { } source
                ? (SetupProfileHistoryKind.VersionRestored, $"Restored version {source} as version {next.Version}")
                : (SetupProfileHistoryKind.VersionSaved, $"Saved version {next.Version}");
            await AddHistoryAsync(profileId, kind, actor, now, change, version: next.Version, ct: ct);
        }
        LogAudit(_logger, restoredFrom is null ? "saved" : "restored", profileId, actor.Upn, actor.ObjectId, updated.CurrentVersion);

        // 8. Prune; failures never fail the save.
        if (next is not null)
        {
            await PruneAsync(profileId, updated.CurrentVersion, ct);
        }
        return new SetupProfileResult(SetupProfileOutcome.Ok, await BuildDetailAsync(updated, ct));
    }

    // ── Delete ──────────────────────────────────────────────────────────────

    /// <summary>Deletes a profile: tombstones the row and removes its targets. Pinned versions stay until the sweep.</summary>
    public async Task<SetupProfileResult> DeleteAsync(Guid profileId, string? changedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(changedBy))
        {
            return SetupProfileResult.BadRequest(MissingActorMessage);
        }
        var profile = await _profiles.GetAsync(profileId, ct);
        if (profile is null || profile.IsDeleted)
        {
            return SetupProfileResult.NotFound();
        }

        var now = _time.GetUtcNow();
        if (!await _profiles.ReplaceAsync(profile with { DeletedAt = now, DeletedBy = changedBy, LastModifiedAt = now, LastModifiedBy = changedBy }, ct))
        {
            var latest = await _profiles.GetAsync(profileId, ct);
            return latest is null || latest.IsDeleted ? SetupProfileResult.NotFound() : SetupProfileResult.Conflict(StaleMessage(latest));
        }

        var keys = await _targets.ListByProfileAsync(profileId, ct);
        await _targets.ApplyChangesAsync(profileId, [], keys, ct);
        await _profiles.ReleaseNameAsync(profile.Name, profileId, ct);
        var actor = new SetupProfileActor { Upn = changedBy };
        await AddHistoryAsync(profileId, SetupProfileHistoryKind.Deleted, actor, now, "Deleted", ct: ct);
        LogAudit(_logger, "deleted", profileId, changedBy, null, profile.CurrentVersion);
        return new SetupProfileResult(SetupProfileOutcome.NoContent);
    }

    // ── Location deletion ───────────────────────────────────────────────────

    /// <summary>
    /// Removes a location that is about to be deleted from the profile that targets it and records
    /// the change, or returns why the deletion is refused (it is the profile's only location). The
    /// profile row's ETag orders this against a concurrent save, so a profile never targets nothing.
    /// </summary>
    public async Task<string?> DetachLocationAsync(Guid locationId, string locationName, string? changedBy, CancellationToken ct = default)
    {
        var key = SetupProfileTargetRepository.LocationKey(locationId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await _targets.GetOwnerAsync(key, ct) is not { } owner)
            {
                return null;
            }
            var profile = await _profiles.GetAsync(owner, ct);
            if (profile is null || profile.IsDeleted)
            {
                await _targets.DeleteAsync(key, ct);
                return null;
            }
            var keys = await _targets.ListByProfileAsync(owner, ct);
            if (keys.Count <= 1)
            {
                return $"{locationName} is the only location of setup profile \"{profile.Name}\". Add another location to the profile or delete the profile, then try again.";
            }

            await _targets.ApplyChangesAsync(owner, [], [key], ct);
            var now = _time.GetUtcNow();
            var actor = new SetupProfileActor { Upn = string.IsNullOrWhiteSpace(changedBy) ? "unknown" : changedBy };
            if (await _profiles.ReplaceAsync(profile with { LastModifiedAt = now, LastModifiedBy = actor.Upn }, ct))
            {
                await AddHistoryAsync(owner, SetupProfileHistoryKind.LocationDeleted, actor, now, $"Removed {locationName} because the location was deleted", removed: [locationId], ct: ct);
                return null;
            }

            // A save changed the profile meanwhile: put the location back and look again.
            await _targets.RevertChangesAsync(owner, [], [key], ct);
        }
        return $"A setup profile that targets {locationName} is being changed. Try again.";
    }

    /// <summary>
    /// Removes a location deleted after <see cref="DetachLocationAsync"/> from a profile that targeted
    /// it again meanwhile, unless that would leave the profile with no location.
    /// </summary>
    public async Task<Guid?> RemoveDeletedLocationAsync(Guid locationId, string locationName, string? changedBy, CancellationToken ct = default)
    {
        var key = SetupProfileTargetRepository.LocationKey(locationId);
        if (await _targets.GetOwnerAsync(key, ct) is not { } owner)
        {
            return null;
        }
        if ((await _targets.ListByProfileAsync(owner, ct)).Count <= 1)
        {
            return null;
        }
        await _targets.DeleteAsync(key, ct);
        var actor = new SetupProfileActor { Upn = string.IsNullOrWhiteSpace(changedBy) ? "unknown" : changedBy };
        await AddHistoryAsync(owner, SetupProfileHistoryKind.LocationDeleted, actor, _time.GetUtcNow(), $"Removed {locationName} because the location was deleted", removed: [locationId], ct: ct);
        return owner;
    }

    // ── Pruning and sweep ───────────────────────────────────────────────────

    /// <summary>
    /// Keeps the newest versions up to the retention count plus every version a running deployment
    /// pins, and deletes the rest. Failures are logged and retried at the next save.
    /// </summary>
    public async Task PruneAsync(Guid profileId, int currentVersion, CancellationToken ct = default)
    {
        try
        {
            var retention = (await _config.GetAsync(ct)).SetupProfileVersionRetention;
            retention = Math.Clamp(retention, PortalConfiguration.MinSetupProfileVersionRetention, PortalConfiguration.MaxSetupProfileVersionRetention);
            var pinned = await _sessions.CountActiveBySetupProfileVersionAsync(profileId, ct);
            var rows = await _versions.ListAsync(profileId, ct);
            var kept = rows.Where(r => r.Version <= currentVersion).Take(retention).Select(r => r.Version).ToHashSet();
            foreach (var row in rows.Where(r => !kept.Contains(r.Version) && r.Version != currentVersion))
            {
                if (!pinned.ContainsKey(row.Version))
                {
                    await _versions.DeleteAsync(row, ct);
                }
                else if (!row.PrunePending)
                {
                    await _versions.SetPrunePendingAsync(row, true, ct);
                }
            }
            foreach (var row in rows.Where(r => r.PrunePending && (kept.Contains(r.Version) || r.Version == currentVersion)))
            {
                await _versions.SetPrunePendingAsync(row, false, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPruneFailed(_logger, ex, profileId);
        }
    }

    /// <summary>
    /// Deletes the versions of deleted profiles once no running deployment pins them, and versions
    /// of other profiles that pruning had to keep only because a deployment pinned them. The
    /// tombstones and history are kept.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var deleted = 0;
        foreach (var profile in await _profiles.ListDeletedAsync(ct))
        {
            var pinned = await _sessions.CountActiveBySetupProfileVersionAsync(profile.ProfileId, ct);
            foreach (var row in await _versions.ListAsync(profile.ProfileId, ct))
            {
                if (!pinned.ContainsKey(row.Version))
                {
                    await _versions.DeleteAsync(row, ct);
                    deleted++;
                }
            }
        }
        foreach (var profile in await _profiles.ListActiveAsync(ct))
        {
            // Only versions the last prune already let go; a changed retention waits for the next save.
            var pending = (await _versions.ListAsync(profile.ProfileId, ct)).Where(r => r.PrunePending && r.Version != profile.CurrentVersion).ToList();
            if (pending.Count == 0)
            {
                continue;
            }
            var pinned = await _sessions.CountActiveBySetupProfileVersionAsync(profile.ProfileId, ct);
            foreach (var row in pending.Where(r => !pinned.ContainsKey(r.Version)))
            {
                await _versions.DeleteAsync(row, ct);
                deleted++;
            }
        }

        // After the versions, so texts only the swept versions referenced go in the same pass.
        var contentDeleted = await SweepContentAsync(ct);
        if (contentDeleted > 0)
        {
            LogContentSwept(_logger, contentDeleted);
        }
        return deleted;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Sizes of the referenced texts and the passes of the answer file, found while validating.</summary>
    internal sealed record ContentFacts(IReadOnlyDictionary<string, long> Sizes, IReadOnlyList<string> Passes)
    {
        public static ContentFacts Empty { get; } = new(new Dictionary<string, long>(), []);
    }

    private async Task<(SetupProfileResult? Invalid, ContentFacts Facts)> ValidateAsync(
        string name, SetupProfileTargeting? targeting, SetupProfileContent? content, SetupProfileContent? current,
        Dictionary<Guid, string> locations, Guid profileId, CancellationToken ct)
    {
        var known = locations.Keys.ToHashSet();
        var problems = SetupProfileValidator.ValidateProfile(name, targeting, known);
        var allowed = SetupProfileValidator.AllowedScriptLocations(targeting, known);
        var facts = ContentFacts.Empty;
        AnswerFileValidationResult? answerFile = null;
        if (content is not null)
        {
            problems.AddRange(SetupProfileValidator.ValidateContent(content, allowed));
            if (problems.Count == 0)
            {
                var sizes = await ResolveSizesAsync(content, current, problems, ct);
                facts = new ContentFacts(sizes, []);
                if (problems.Count == 0 && content.AnswerFile is { } file)
                {
                    // Re-validated from what is stored, whatever the portal checked before the save.
                    var text = await _content.DownloadAsync(file.ContentSha256, ct);
                    answerFile = text is null ? null : AnswerFileValidator.Validate(text.ToString());
                    if (answerFile is null)
                    {
                        problems.Add(SetupProfileValidator.AnswerFileNotUploadedMessage);
                    }
                    facts = facts with { Passes = answerFile?.Passes ?? [] };
                }
            }
        }
        else if (current is not null && targeting is not null)
        {
            problems.AddRange(SetupProfileValidator.ScriptsWithoutLocations(current.Scripts, allowed));
        }

        if (problems.Count > 0)
        {
            return (SetupProfileResult.BadRequest(string.Join(' ', problems)), facts);
        }
        if (answerFile is { IsValid: false })
        {
            var count = answerFile.Problems.Count;
            var detail = count == 1 ? "The answer file has 1 problem." : $"The answer file has {count} problems.";
            return (SetupProfileResult.BadRequest(detail, answerFile.Problems), facts);
        }

        if (await _profiles.FindActiveByNameAsync(name, ct) is { } existing && existing.ProfileId != profileId)
        {
            return (DuplicateName(name), facts);
        }
        return (null, facts);
    }

    /// <summary>
    /// Size of every referenced text. Texts the current version already references are known to be
    /// stored; the rest are looked up, and a missing or oversized one is a problem naming its script.
    /// </summary>
    private async Task<Dictionary<string, long>> ResolveSizesAsync(SetupProfileContent content, SetupProfileContent? current, List<string> problems, CancellationToken ct)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var script in current?.Scripts ?? [])
        {
            sizes.TryAdd(script.ContentSha256, script.SizeBytes);
        }
        if (current?.AnswerFile is { } currentFile)
        {
            sizes.TryAdd(currentFile.ContentSha256, currentFile.SizeBytes);
        }

        var unknown = content.Scripts.Select(s => s.ContentSha256)
            .Append(content.AnswerFile?.ContentSha256)
            .OfType<string>()
            .Where(sha => !sizes.ContainsKey(sha))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(unknown, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (sha, token) =>
        {
            if (await _content.GetInfoAsync(sha, token) is { } info)
            {
                found[sha] = info.SizeBytes;
            }
        });
        foreach (var (sha, size) in found)
        {
            sizes[sha] = size;
        }

        foreach (var script in content.Scripts)
        {
            if (!sizes.TryGetValue(script.ContentSha256, out var size) || size > SetupProfileContent.MaxScriptBytes)
            {
                problems.Add(SetupProfileValidator.NotUploadedMessage(script.Name));
            }
        }
        if (content.AnswerFile is { } file && (!sizes.TryGetValue(file.ContentSha256, out var fileSize) || fileSize > SetupProfileContent.MaxAnswerFileBytes))
        {
            problems.Add(SetupProfileValidator.AnswerFileNotUploadedMessage);
        }
        return sizes;
    }

    private async Task<SetupProfileResult> TargetConflictAsync(SetupProfileTargetChangeResult result, Dictionary<Guid, string> locations, CancellationToken ct)
    {
        var owner = result.ConflictOwner is { } id ? await _profiles.GetAsync(id, ct) : null;
        var ownerName = owner?.Name ?? "another profile";
        if (result.ConflictTarget == SetupProfileTargetRepository.AllDevicesKey)
        {
            return SetupProfileResult.Conflict($"All devices is already used by the setup profile \"{ownerName}\". Change that profile first.");
        }
        var locationName = Guid.TryParse(result.ConflictTarget, out var locationId) && locations.TryGetValue(locationId, out var found) ? found : "A location";
        return SetupProfileResult.Conflict($"{locationName} is already targeted by the setup profile \"{ownerName}\". Remove it from that profile first.");
    }

    /// <summary>
    /// Claims targets, releasing first any row still held by a profile that was deleted or never
    /// stored, so a partial failure cannot reserve a location for ever.
    /// </summary>
    private async Task<SetupProfileTargetChangeResult> ClaimTargetsAsync(Guid profileId, List<string> adds, List<string> deletes, CancellationToken ct)
    {
        // Each pass can only release one row, so allow one attempt per requested target.
        for (var attempt = 0; attempt <= adds.Count; attempt++)
        {
            var result = await _targets.ApplyChangesAsync(profileId, adds, deletes, ct);
            if (result.Succeeded || !await ReleaseStaleTargetAsync(result, ct))
            {
                return result;
            }
        }
        return await _targets.ApplyChangesAsync(profileId, adds, deletes, ct);
    }

    /// <summary>True when the conflicting target was held by a profile that no longer exists, and was released.</summary>
    private async Task<bool> ReleaseStaleTargetAsync(SetupProfileTargetChangeResult conflict, CancellationToken ct)
    {
        if (conflict.ConflictTarget is not { } key || conflict.ConflictOwner is not { } ownerId)
        {
            return false;
        }
        var owner = await _profiles.GetAsync(ownerId, ct);
        if (owner is not null && !owner.IsDeleted)
        {
            return false;
        }
        LogStaleTargetReleased(_logger, key, ownerId);
        await _targets.DeleteAsync(key, ct);
        return true;
    }

    private async Task<SetupProfileVersionRecord> WriteVersionAsync(
        SetupProfileContent content, SetupProfileContent? previous, DateTimeOffset now, string author, int? restoredFrom, SetupProfileDuplicatedFrom? duplicatedFrom, CancellationToken ct)
    {
        var blob = SetupProfileVersionRepository.Serialize(content);
        var row = new SetupProfileVersionRecord
        {
            ProfileId = content.ProfileId,
            Version = content.Version,
            CreatedAt = now,
            CreatedBy = author,
            BlobName = SetupProfileVersionRepository.BlobNameFor(content.ProfileId, content.Version),
            ContentSha256 = blob.Sha256,
            ContentSizeBytes = blob.Bytes.LongLength,
            RestoredFrom = restoredFrom,
            DuplicatedFrom = duplicatedFrom,
            ChangeSummary = ChangeSummaryBuilder.Build(previous, content),
        };
        await _versions.UploadAsync(row.BlobName, blob, ct);
        await _versions.AddAsync(row, ct);
        return row;
    }

    /// <summary>
    /// Assigns ids to new items, fills in sizes and passes, and carries per-item modified fields
    /// over from <paramref name="previous"/> for items that did not change.
    /// </summary>
    internal static SetupProfileContent Normalize(SetupProfileContent requested, SetupProfileContent? previous, Guid profileId, int version, DateTimeOffset now, string author, ContentFacts facts)
    {
        var usedIds = new HashSet<Guid>();
        Guid Assign(Guid? id) => id is { } value && value != Guid.Empty && usedIds.Add(value) ? value : NewId(usedIds);

        var previousScripts = previous?.Scripts.Where(s => s.Id is not null).ToDictionary(s => s.Id!.Value) ?? [];
        var registryValues = requested.RegistryValues
            .Select(v => v with { Id = Assign(v.Id), Name = v.Name.Trim(), Key = v.Key.Trim(), ValueName = v.ValueName ?? string.Empty, Data = v.Data ?? string.Empty })
            .ToList();
        usedIds.Clear();
        var scripts = requested.Scripts
            .OrderBy(s => s.Runs).ThenBy(s => s.Order)
            .Select(s =>
            {
                var id = Assign(s.Id);
                var locations = s.Locations is null || s.Locations.All
                    ? new SetupScriptLocations()
                    : new SetupScriptLocations { All = false, LocationIds = [.. s.Locations.LocationIds.Distinct().Order()] };
                var unchanged = previousScripts.TryGetValue(id, out var old) && old.ContentSha256 == s.ContentSha256 && old.Name == s.Name.Trim()
                    && old.Runs == s.Runs && old.TimeoutMinutes == s.TimeoutMinutes && ChangeSummaryBuilder.SameLocations(old.Locations, locations);
                return s with
                {
                    Id = id,
                    Name = s.Name.Trim(),
                    Locations = locations,
                    SizeBytes = facts.Sizes.GetValueOrDefault(s.ContentSha256),
                    LastModifiedAt = unchanged ? old!.LastModifiedAt : now,
                    LastModifiedBy = unchanged ? old!.LastModifiedBy : author,
                };
            })
            .ToList();

        SetupAnswerFile? answerFile = null;
        if (requested.AnswerFile is { } file)
        {
            var unchanged = previous?.AnswerFile is { } old && old.ContentSha256 == file.ContentSha256;
            answerFile = new SetupAnswerFile
            {
                ContentSha256 = file.ContentSha256,
                Passes = facts.Passes,
                SizeBytes = facts.Sizes.GetValueOrDefault(file.ContentSha256),
                LastModifiedAt = unchanged ? previous!.AnswerFile!.LastModifiedAt : now,
                LastModifiedBy = unchanged ? previous!.AnswerFile!.LastModifiedBy : author,
            };
        }

        return new SetupProfileContent
        {
            SchemaVersion = SetupProfileContent.CurrentSchemaVersion,
            ProfileId = profileId,
            Version = version,
            FailureHandling = requested.FailureHandling,
            RegistryValues = registryValues,
            Scripts = scripts,
            AnswerFile = answerFile,
        };
    }

    private static Guid NewId(HashSet<Guid> used)
    {
        var id = Guid.NewGuid();
        used.Add(id);
        return id;
    }

    internal static bool SameContent(SetupProfileContent a, SetupProfileContent b) =>
        Comparable(a) == Comparable(b);

    private static string Comparable(SetupProfileContent c) => JsonSerializer.Serialize(new
    {
        c.FailureHandling,
        RegistryValues = c.RegistryValues.Select(v => new { v.Id, v.Name, v.Hive, v.Key, v.ValueName, v.Type, v.Data }),
        Scripts = c.Scripts.Select(s => new { s.Id, s.Name, s.Runs, s.Order, s.TimeoutMinutes, s.ContentSha256, s.Locations.All, LocationIds = s.Locations.All ? new List<Guid>() : [.. s.Locations.LocationIds.Order()] }),
        AnswerFile = c.AnswerFile?.ContentSha256,
    });

    // ── Content ─────────────────────────────────────────────────────────────

    /// <summary>Message when an uploaded answer file is over its limit.</summary>
    public static string AnswerFileTooLargeMessage => AnswerFileValidator.Messages["tooLarge"];

    /// <summary>"This script is 25.3 MB. A script can be at most 24 MB.", the size rounded up to a tenth.</summary>
    public static string ScriptTooLargeMessage(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"This script is {Math.Ceiling(bytes / 1_048_576d * 10) / 10:0.0} MB. A script can be at most 24 MB.");

    /// <summary>
    /// Stores an uploaded script or answer file text. Reads at most the limit plus one byte (a
    /// leading BOM is dropped and not counted); an answer file is also checked against the answer
    /// file rules, whose problems block the save but not the upload.
    /// </summary>
    /// <param name="body">Request body.</param>
    /// <param name="kind"><c>script</c> or <c>answerFile</c>.</param>
    /// <param name="declaredLength">Content-Length, used for the size in the too-large message.</param>
    /// <param name="uploadedBy">UPN of the uploading user, for the audit log.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SetupProfileResult> UploadContentAsync(Stream body, string? kind, long? declaredLength, string? uploadedBy = null, CancellationToken ct = default)
    {
        var isScript = string.Equals(kind, SetupProfileContentRepository.ScriptKind, StringComparison.Ordinal);
        if (!isScript && !string.Equals(kind, SetupProfileContentRepository.AnswerFileKind, StringComparison.Ordinal))
        {
            return SetupProfileResult.BadRequest("Upload a script or an answer file.");
        }
        var limit = isScript ? SetupProfileContent.MaxScriptBytes : SetupProfileContent.MaxAnswerFileBytes;

        // Room for a BOM plus one byte over the limit, which is enough to tell a text is too large.
        // Sized from Content-Length and grown while reading, so a small upload never takes 24 MiB.
        var capacity = limit + 4;
        var buffer = new byte[declaredLength is > 0 ? (int)Math.Min(declaredLength.Value + 1, capacity) : Math.Min(81_920, capacity)];
        var read = 0;
        int n;
        while (read < capacity)
        {
            if (read == buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, capacity));
            }
            if ((n = await body.ReadAsync(buffer.AsMemory(read), ct)) == 0)
            {
                break;
            }
            read += n;
        }
        var bom = read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
        var size = read - bom;
        if (size > limit)
        {
            if (!isScript)
            {
                return new SetupProfileResult(SetupProfileOutcome.PayloadTooLarge, Detail: AnswerFileTooLargeMessage);
            }
            var total = declaredLength is > 0 ? declaredLength.Value - bom : read + await CountRemainingAsync(body, ct) - bom;
            return new SetupProfileResult(SetupProfileOutcome.PayloadTooLarge, Detail: ScriptTooLargeMessage(total));
        }

        var bytes = buffer.AsMemory(bom, size);
        if (bytes.Span.TrimStart(" \t\r\n"u8).IsEmpty)
        {
            return SetupProfileResult.BadRequest(isScript ? "The script is empty. Add the script text." : "The answer file is empty. Add the answer file text.");
        }

        var stored = await _content.StoreAsync(bytes, isScript ? SetupProfileContentRepository.ScriptKind : SetupProfileContentRepository.AnswerFileKind, _time.GetUtcNow(), ct);
        LogContentUploaded(_logger, kind!, stored.Sha256, stored.SizeBytes, string.IsNullOrWhiteSpace(uploadedBy) ? "unknown" : uploadedBy);
        if (isScript)
        {
            return new SetupProfileResult(SetupProfileOutcome.Ok, new SetupProfileContentUploadResult { Sha256 = stored.Sha256, SizeBytes = stored.SizeBytes });
        }
        var validation = AnswerFileValidator.Validate(Encoding.UTF8.GetString(bytes.Span));
        return new SetupProfileResult(SetupProfileOutcome.Ok, new SetupProfileContentUploadResult
        {
            Sha256 = stored.Sha256,
            SizeBytes = stored.SizeBytes,
            Passes = validation.Passes,
            Problems = [.. validation.Problems.Select(p => new SetupProfileContentProblem { Code = p.Code, Line = p.Line, Message = p.Message })],
        });
    }

    private static async Task<long> CountRemainingAsync(Stream body, CancellationToken ct)
    {
        var scratch = new byte[81_920];
        long count = 0;
        int n;
        while ((n = await body.ReadAsync(scratch, ct)) > 0)
        {
            count += n;
        }
        return count;
    }

    /// <summary>
    /// Returns a stored text when a retained version of the profile references it, so a profile's
    /// readers cannot read texts that belong only to other profiles.
    /// </summary>
    public async Task<BinaryData?> GetContentAsync(Guid profileId, string sha256, CancellationToken ct = default)
    {
        if (!SetupProfileContentRepository.IsSha256(sha256))
        {
            return null;
        }
        var profile = await _profiles.GetAsync(profileId, ct);
        if (profile is null || profile.IsDeleted)
        {
            return null;
        }
        foreach (var row in await _versions.ListAsync(profileId, ct))
        {
            if (await _versions.DownloadAsync(row.BlobName, ct) is { } content && References(content).Contains(sha256))
            {
                return await _content.DownloadAsync(sha256, ct);
            }
        }
        return null;
    }

    private static HashSet<string> References(SetupProfileContent content)
    {
        var hashes = content.Scripts.Select(s => s.ContentSha256).ToHashSet(StringComparer.Ordinal);
        if (content.AnswerFile is { } file)
        {
            hashes.Add(file.ContentSha256);
        }
        return hashes;
    }

    /// <summary>
    /// At most once a day: deletes texts uploaded more than a day ago that no retained version of any
    /// profile, deleted ones included, references.
    /// </summary>
    public async Task<int> SweepContentAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (_lastContentSweep is { } last && now - last < ContentSweepInterval)
        {
            return 0;
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in (await _profiles.ListActiveAsync(ct)).Concat(await _profiles.ListDeletedAsync(ct)))
        {
            foreach (var row in await _versions.ListAsync(profile.ProfileId, ct))
            {
                if (await _versions.DownloadAsync(row.BlobName, ct) is { } content)
                {
                    referenced.UnionWith(References(content));
                }
            }
        }

        var deleted = 0;
        await foreach (var item in _content.ListAsync(ct))
        {
            if (!referenced.Contains(item.Sha256) && (item.UploadedAt is not { } uploaded || now - uploaded >= ContentGracePeriod))
            {
                await _content.DeleteAsync(item.Sha256, ct);
                deleted++;
            }
        }
        _lastContentSweep = now;
        return deleted;
    }

    private async Task<SetupProfileDetail> BuildDetailAsync(SetupProfileRecord profile, CancellationToken ct)
    {
        var owners = await _targets.ListAllAsync(ct);
        var row = await _versions.GetAsync(profile.ProfileId, profile.CurrentVersion, ct);
        var content = row is null ? null : await _versions.DownloadAsync(row.BlobName, ct);
        var summary = ToSummary(profile, TargetingFrom(owners, profile.ProfileId));
        return new SetupProfileDetail
        {
            ProfileId = summary.ProfileId,
            Name = summary.Name,
            Targeting = summary.Targeting,
            RegistryValueCount = summary.RegistryValueCount,
            ScriptCount = summary.ScriptCount,
            HasAnswerFile = summary.HasAnswerFile,
            FailureHandling = summary.FailureHandling,
            CurrentVersion = summary.CurrentVersion,
            LastModifiedAt = summary.LastModifiedAt,
            LastModifiedBy = summary.LastModifiedBy,
            CreatedAt = profile.CreatedAt,
            CreatedBy = profile.CreatedBy,
            Content = content ?? new SetupProfileContent { ProfileId = profile.ProfileId, Version = profile.CurrentVersion },
        };
    }

    private static SetupProfileSummary ToSummary(SetupProfileRecord p, SetupProfileTargeting targeting) => new()
    {
        ProfileId = p.ProfileId,
        Name = p.Name,
        Targeting = targeting,
        RegistryValueCount = p.RegistryValueCount,
        ScriptCount = p.ScriptCount,
        HasAnswerFile = p.HasAnswerFile,
        FailureHandling = p.FailureHandling,
        CurrentVersion = p.CurrentVersion,
        LastModifiedAt = p.LastModifiedAt,
        LastModifiedBy = p.LastModifiedBy,
    };

    private static SetupProfileVersionSummary ToVersionSummary(SetupProfileVersionRecord row, int currentVersion, Dictionary<int, int> inUse) => new()
    {
        Version = row.Version,
        CreatedAt = row.CreatedAt,
        CreatedBy = row.CreatedBy,
        ChangeSummary = row.ChangeSummary,
        RestoredFrom = row.RestoredFrom,
        DuplicatedFrom = row.DuplicatedFrom,
        IsCurrent = row.Version == currentVersion,
        InUseCount = inUse.GetValueOrDefault(row.Version),
    };

    private static SetupProfileTargeting TargetingFrom(Dictionary<string, Guid> owners, Guid profileId)
    {
        var keys = owners.Where(o => o.Value == profileId).Select(o => o.Key).ToList();
        return new SetupProfileTargeting
        {
            AllDevices = keys.Contains(SetupProfileTargetRepository.AllDevicesKey, StringComparer.OrdinalIgnoreCase),
            LocationIds = [.. keys.Select(k => Guid.TryParse(k, out var id) ? id : (Guid?)null).OfType<Guid>().Order()],
        };
    }

    private static HashSet<string> Keys(SetupProfileTargeting targeting) => targeting.AllDevices
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SetupProfileTargetRepository.AllDevicesKey }
        : targeting.LocationIds.Select(SetupProfileTargetRepository.LocationKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static (List<string> Adds, List<string> Deletes) TargetChanges(HashSet<string> before, SetupProfileTargeting after)
    {
        var next = Keys(after);
        return ([.. next.Where(k => !before.Contains(k))], [.. before.Where(k => !next.Contains(k))]);
    }

    private static List<Guid> LocationIds(IEnumerable<string> keys) =>
        [.. keys.Select(k => Guid.TryParse(k, out var id) ? id : (Guid?)null).OfType<Guid>()];

    private async Task<Dictionary<Guid, string>> LoadLocationsAsync(CancellationToken ct)
    {
        var locations = new Dictionary<Guid, string>();
        await foreach (var location in _locations.ListAllAsync(ct))
        {
            locations[location.LocationId] = location.Name;
        }
        return locations;
    }

    private static string DescribeTargeting(SetupProfileTargeting targeting, Dictionary<Guid, string> locations) =>
        targeting.AllDevices ? "All devices" : JoinNames(targeting.LocationIds.Select(id => locations.GetValueOrDefault(id, "an unknown location")));

    private static string DescribeTargetingChange(SetupProfileTargeting before, SetupProfileTargeting after, Dictionary<Guid, string> locations)
    {
        if (before.AllDevices && !after.AllDevices)
        {
            return $"Targeting changed from All devices to Selected locations: {DescribeTargeting(after, locations)}";
        }
        if (!before.AllDevices && after.AllDevices)
        {
            return "Targeting changed from Selected locations to All devices";
        }
        var added = after.LocationIds.Except(before.LocationIds).Select(id => locations.GetValueOrDefault(id, "an unknown location")).ToList();
        var removed = before.LocationIds.Except(after.LocationIds).Select(id => locations.GetValueOrDefault(id, "a deleted location")).ToList();
        var parts = new List<string>();
        if (added.Count > 0)
        {
            parts.Add($"Added {JoinNames(added)}");
        }
        if (removed.Count > 0)
        {
            parts.Add($"Removed {JoinNames(removed)}");
        }
        return string.Join(". ", parts);
    }

    private static string JoinNames(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count switch
        {
            0 => "nothing",
            1 => list[0],
            _ => $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}",
        };
    }

    private static string StaleMessage(SetupProfileRecord latest) =>
        string.Create(CultureInfo.InvariantCulture, $"This profile was changed by {latest.LastModifiedBy ?? "another user"} at {latest.LastModifiedAt.UtcDateTime:yyyy-MM-dd HH:mm}. Reload to see their changes.");

    private static SetupProfileResult DuplicateName(string name) =>
        SetupProfileResult.Conflict($"A setup profile named \"{name}\" already exists. Choose another name.");

    private Task AddHistoryAsync(
        Guid profileId, SetupProfileHistoryKind kind, SetupProfileActor actor, DateTimeOffset at, string change,
        IReadOnlyList<Guid>? added = null, IReadOnlyList<Guid>? removed = null, int? version = null, CancellationToken ct = default) =>
        _history.AddAsync(new SetupProfileHistoryRecord
        {
            ProfileId = profileId,
            Kind = kind,
            At = at,
            By = actor.Upn,
            ByObjectId = actor.ObjectId,
            Change = change,
            AddedLocationIds = added ?? [],
            RemovedLocationIds = removed ?? [],
            Version = version,
        }, ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup profile {ProfileId} {Action} by {Upn} ({ObjectId}); current version {Version}.")]
    private static partial void LogAuditCore(ILogger logger, Guid profileId, string action, string upn, string? objectId, int version);

    private static void LogAudit(ILogger logger, string action, Guid profileId, string upn, string? objectId, int version) =>
        LogAuditCore(logger, profileId, action, upn, objectId, version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Pruning old versions of setup profile {ProfileId} failed; it is retried at the next save.")]
    private static partial void LogPruneFailed(ILogger logger, Exception ex, Guid profileId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} uploaded setup profile texts that no version uses.")]
    private static partial void LogContentSwept(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup profile {Kind} text {Sha256} ({SizeBytes} bytes) uploaded by {Upn}.")]
    private static partial void LogContentUploaded(ILogger logger, string kind, string sha256, long sizeBytes, string upn);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Released setup profile target {TargetKey} still held by profile {ProfileId}, which no longer exists.")]
    private static partial void LogStaleTargetReleased(ILogger logger, string targetKey, Guid profileId);
}
