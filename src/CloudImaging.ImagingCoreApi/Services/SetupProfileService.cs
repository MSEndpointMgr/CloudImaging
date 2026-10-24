using System.Globalization;
using System.Security.Cryptography;
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

    private readonly SetupProfileRepository _profiles;
    private readonly SetupProfileTargetRepository _targets;
    private readonly SetupProfileVersionRepository _versions;
    private readonly SetupProfileHistoryRepository _history;
    private readonly LocationRepository _locations;
    private readonly DeviceSessionRepository _sessions;
    private readonly PortalConfigurationRepository _config;
    private readonly TimeProvider _time;
    private readonly ILogger<SetupProfileService> _logger;

    /// <param name="profiles">Profile rows.</param>
    /// <param name="targets">Targeting index.</param>
    /// <param name="versions">Version rows and documents.</param>
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
        var invalid = await ValidateAsync(name, request.Targeting, request.Content, locations, profileId, ct);
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
        var targetResult = await _targets.ApplyChangesAsync(profileId, adds, deletes, ct);
        if (!targetResult.Succeeded)
        {
            await _profiles.ReleaseNameAsync(name, profileId, CancellationToken.None);
            return await TargetConflictAsync(targetResult, locations, ct);
        }

        var content = Normalize(request.Content, null, profileId, 1, now, actor.Upn);
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
        var invalid = await ValidateAsync(name, targeting, requestedContent, locations, profileId, ct);
        if (invalid is not null)
        {
            return invalid;
        }

        // 3. Decide whether the content needs a new version.
        var now = _time.GetUtcNow();
        var currentRow = await _versions.GetAsync(profileId, profile.CurrentVersion, ct);
        var current = currentRow is null ? null : await _versions.DownloadAsync(currentRow.BlobName, ct);
        SetupProfileContent? next = null;
        if (requestedContent is not null)
        {
            var candidate = Normalize(requestedContent, current, profileId, profile.CurrentVersion + 1, now, actor.Upn);
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
        var targetResult = await _targets.ApplyChangesAsync(profileId, adds, deletes, ct);
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
        return deleted;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<SetupProfileResult?> ValidateAsync(
        string name, SetupProfileTargeting? targeting, SetupProfileContent? content, Dictionary<Guid, string> locations, Guid profileId, CancellationToken ct)
    {
        var problems = SetupProfileValidator.ValidateProfile(name, targeting, locations.Keys.ToHashSet());
        AnswerFileValidationResult? answerFile = null;
        if (content is not null)
        {
            problems.AddRange(SetupProfileValidator.ValidateContent(content));
            if (content.AnswerFile is { } file)
            {
                answerFile = AnswerFileValidator.Validate(file.Content ?? string.Empty);
            }
        }

        if (problems.Count > 0)
        {
            return SetupProfileResult.BadRequest(string.Join(' ', problems));
        }
        if (answerFile is { IsValid: false })
        {
            var count = answerFile.Problems.Count;
            var detail = count == 1 ? "The answer file has 1 problem." : $"The answer file has {count} problems.";
            return SetupProfileResult.BadRequest(detail, answerFile.Problems);
        }

        if (await _profiles.FindActiveByNameAsync(name, ct) is { } existing && existing.ProfileId != profileId)
        {
            return DuplicateName(name);
        }
        return null;
    }

    private async Task<SetupProfileResult> TargetConflictAsync(SetupProfileTargetChangeResult result, Dictionary<Guid, string> locations, CancellationToken ct)
    {
        var owner = result.ConflictOwner is { } id ? await _profiles.GetAsync(id, ct) : null;
        var ownerName = owner?.Name ?? "another profile";
        if (result.ConflictTarget == SetupProfileTargetRepository.AllDevicesKey)
        {
            return SetupProfileResult.Conflict($"All devices is already used by \"{ownerName}\". Change that profile first.");
        }
        var locationName = Guid.TryParse(result.ConflictTarget, out var locationId) && locations.TryGetValue(locationId, out var found) ? found : "A location";
        return SetupProfileResult.Conflict($"{locationName} is already targeted by \"{ownerName}\". Remove it there first.");
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
    /// Assigns ids to new items, computes hashes, sizes and passes, and carries per-item modified
    /// fields over from <paramref name="previous"/> for items that did not change.
    /// </summary>
    internal static SetupProfileContent Normalize(SetupProfileContent requested, SetupProfileContent? previous, Guid profileId, int version, DateTimeOffset now, string author)
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
                var unchanged = previousScripts.TryGetValue(id, out var old) && old.Content == s.Content && old.Name == s.Name.Trim()
                    && old.Runs == s.Runs && old.TimeoutMinutes == s.TimeoutMinutes;
                return s with
                {
                    Id = id,
                    Name = s.Name.Trim(),
                    Sha256 = Sha256(Encoding.UTF8.GetBytes(s.Content)),
                    LastModifiedAt = unchanged ? old!.LastModifiedAt : now,
                    LastModifiedBy = unchanged ? old!.LastModifiedBy : author,
                };
            })
            .ToList();

        SetupAnswerFile? answerFile = null;
        if (requested.AnswerFile is { } file)
        {
            var unchanged = previous?.AnswerFile is { } old && old.Content == file.Content;
            answerFile = new SetupAnswerFile
            {
                Content = file.Content,
                Passes = AnswerFileValidator.Validate(file.Content).Passes,
                SizeBytes = Encoding.UTF8.GetByteCount(file.Content),
                Sha256 = Sha256(Encoding.UTF8.GetBytes(file.Content)),
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
        Scripts = c.Scripts.Select(s => new { s.Id, s.Name, s.Runs, s.Order, s.TimeoutMinutes, s.Content }),
        AnswerFile = c.AnswerFile?.Content,
    });

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

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
}
