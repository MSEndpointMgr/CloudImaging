using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// Custom components (FR-094 to FR-099): a driver the vendor has not published yet, uploaded by an
/// Operator for one or more models, optionally replacing one vendor component on each until it is
/// removed. Packages are staged by the browser straight into the <c>drivers</c> container.
/// </summary>
public sealed class DriverCustomComponentService
{
    /// <summary>Largest package.</summary>
    public const long MaxPackageBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>Block size the browser stages with.</summary>
    public const int UploadBlockSize = 8 * 1024 * 1024;

    /// <summary>Lifetime of the staging write link.</summary>
    public static readonly TimeSpan UploadLinkLifetime = TimeSpan.FromHours(4);

    private static readonly string[] Extensions = [".zip", ".exe", ".msi"];

    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverStorageService _storage;
    private readonly DriverPolicyService _policy;
    private readonly TimeProvider _time;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="storage">Staging and package checks.</param>
    /// <param name="policy">Marks pairs dirty.</param>
    /// <param name="time">Clock.</param>
    public DriverCustomComponentService(DriverStore store, DriverModelRepository models, DriverStorageService storage, DriverPolicyService policy, TimeProvider time)
    {
        _store = store;
        _models = models;
        _storage = storage;
        _policy = policy;
        _time = time;
    }

    /// <summary>Lineage key of a custom component on its models.</summary>
    public static string LineageKey(Guid customId) => $"custom-{customId:N}";

    /// <summary>Starts a staged upload; 400 over 5 GB or for another extension.</summary>
    public async Task<DriverResult> StartUploadAsync(StartDriverCustomUploadRequest? request, CancellationToken ct = default)
    {
        var fileName = SafeFileName(request?.FileName);
        if (fileName is null || !Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase))
        {
            return DriverResult.BadRequest("Choose a .zip, .exe or .msi file.");
        }

        if (request!.SizeBytes is <= 0 or > MaxPackageBytes)
        {
            return DriverResult.BadRequest("The package must be 5 GB or smaller.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var uploadId = Guid.NewGuid();
        var stagingBlob = $"custom/staging/{uploadId:N}/{fileName}";
        await _store.CustomUploads.UpsertAsync(DriverStore.UploadPartition, uploadId.ToString("N"), new DriverCustomUploadRecord
        {
            FileName = fileName,
            SizeBytes = request.SizeBytes,
            BlobName = $"custom/{uploadId:N}/{fileName}",
            StagingBlobName = stagingBlob,
            StartedBy = upn,
            StartedAt = _time.GetUtcNow(),
        }, ct);
        return DriverResult.Ok(new DriverCustomUploadStarted
        {
            UploadId = uploadId,
            UploadUrl = await _storage.GetUploadLinkAsync(stagingBlob, UploadLinkLifetime, ct),
            BlockSize = UploadBlockSize,
        });
    }

    // An upload belongs to the Operator who started it; anyone else is told it does not exist.
    private async Task<Stored<DriverCustomUploadRecord>?> OwnUploadAsync(Guid uploadId, string? upn, CancellationToken ct)
    {
        var row = await _store.CustomUploads.GetAsync(DriverStore.UploadPartition, uploadId.ToString("N"), ct);
        return row is not null && upn is { Length: > 0 } && (row.Value.StartedBy is null || string.Equals(row.Value.StartedBy, upn, StringComparison.OrdinalIgnoreCase)) ? row : null;
    }

    /// <summary>Commits a staged upload and checks it: SHA-256, size and, for a ZIP, its INF files.</summary>
    public async Task<DriverResult> CompleteUploadAsync(Guid uploadId, CompleteDriverCustomUploadRequest? request, CancellationToken ct = default)
    {
        var row = await OwnUploadAsync(uploadId, request?.Actor?.Upn, ct);
        if (row is null)
        {
            return DriverResult.NotFound("Upload not found.");
        }

        var upload = row.Value;
        if (upload.Completed)
        {
            return upload.CustomId is null
                ? DriverResult.Ok(new DriverCustomUploadCompleted { Sha256 = upload.Sha256!, SizeBytes = upload.SizeBytes, InfCount = upload.InfCount })
                : DriverResult.Conflict("This upload is already in use. Upload the package again.");
        }

        if (request!.BlockIds is not { Count: > 0 } blockIds)
        {
            return DriverResult.BadRequest("The upload is incomplete. Upload the package again.");
        }

        var zip = upload.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        var (sha256, size, infCount, failure) = await _storage.CommitPackageAsync(upload.StagingBlobName ?? upload.BlobName, blockIds, zip, upload.BlobName, MaxPackageBytes, upload.SizeBytes, ct);
        if (failure is not null || sha256 is null)
        {
            return DriverResult.BadRequest(failure ?? "The package could not be read.");
        }

        await _store.CustomUploads.UpsertAsync(DriverStore.UploadPartition, row.RowKey, upload with { Completed = true, Sha256 = sha256, SizeBytes = size, InfCount = infCount }, ct);
        return DriverResult.Ok(new DriverCustomUploadCompleted { Sha256 = sha256, SizeBytes = size, InfCount = infCount });
    }

    /// <summary>Deletes a staged upload that no custom component uses.</summary>
    public async Task<DriverResult> AbandonUploadAsync(Guid uploadId, string? upn, CancellationToken ct = default)
    {
        var row = await OwnUploadAsync(uploadId, upn, ct);
        if (row is { Value.CustomId: null })
        {
            await DeleteUploadAsync(row, ct);
        }

        return new DriverResult(HttpStatusCode.NoContent);
    }

    private async Task DeleteUploadAsync(Stored<DriverCustomUploadRecord> row, CancellationToken ct)
    {
        if (row.Value.StagingBlobName is { } staging)
        {
            await _storage.DeleteAsync(staging, ct);
        }

        await _storage.DeleteAsync(row.Value.BlobName, ct);
        await _store.CustomUploads.DeleteAsync(DriverStore.UploadPartition, row.RowKey, ct: ct);
    }


    /// <summary>Every custom component with its models.</summary>
    public async Task<IReadOnlyList<DriverCustomComponent>> ListAsync(CancellationToken ct = default)
    {
        var models = (await _models.ListAsync(ct)).ToDictionary(m => m.ModelId);
        var result = new List<DriverCustomComponent>();
        foreach (var row in (await _store.CustomComponents.ListPartitionAsync(DriverStore.CustomPartition, ct)).OrderBy(r => r.Value.Name, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(await ToApiAsync(row.Value, models, ct));
        }

        return result;
    }

    /// <summary>Adds a custom component; 400 with field errors, 409 when a lineage is already replaced.</summary>
    public async Task<DriverResult> CreateAsync(DriverCustomComponentRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        // Only the caller's own upload that no custom component uses yet.
        var upload = request.UploadId is { } id && (await OwnUploadAsync(id, upn, ct))?.Value is { CustomId: null } own ? own : null;
        var (errors, targets) = await ValidateAsync(request, upload, null, ct);
        if (errors.Count > 0)
        {
            return FieldErrors(errors);
        }

        var customId = request.UploadId!.Value;
        if (await ReplacementConflictAsync(customId, targets, ct) is { } conflict)
        {
            return conflict;
        }

        var now = _time.GetUtcNow();
        var record = new DriverCustomComponentRecord
        {
            CustomId = customId,
            Name = request.Name!.Trim(),
            Version = request.Version!.Trim(),
            Category = request.Category!.Trim(),
            Action = request.Action!.Value,
            BlobName = upload!.BlobName,
            Sha256 = upload.Sha256!,
            Size = upload.SizeBytes,
            FileName = upload.FileName,
            InfCount = upload.InfCount,
            InstallCommand = ParseCommand(request.InstallCommand),
            UploadedAt = now,
            UploadedBy = upn,
        };
        await _store.CustomComponents.UpsertAsync(DriverStore.CustomPartition, customId.ToString("N"), record, ct);
        await _store.CustomUploads.UpsertAsync(DriverStore.UploadPartition, customId.ToString("N"), upload with { CustomId = customId }, ct);
        await WriteTargetsAsync(customId, [], targets, ct);
        await _store.LogAsync(now, upn, request.Actor.ObjectId, "Custom component", record.Name, $"Added {record.Version} to {Names(targets)}", ct);
        await MarkDirtyAsync(targets.Select(t => t.Model), now, ct);
        var models = (await _models.ListAsync(ct)).ToDictionary(m => m.ModelId);
        return new DriverResult(HttpStatusCode.Created, await ToApiAsync(record, models, ct));
    }

    /// <summary>Changes a custom component; a new package comes with a new upload.</summary>
    public async Task<DriverResult> UpdateAsync(Guid customId, DriverCustomComponentRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var existing = (await _store.CustomComponents.GetAsync(DriverStore.CustomPartition, customId.ToString("N"), ct))?.Value;
        if (existing is null)
        {
            return DriverResult.NotFound("Custom component not found.");
        }

        var upload = request.UploadId is { } id && id != customId && (await OwnUploadAsync(id, upn, ct))?.Value is { CustomId: null } own ? own : null;
        var (errors, targets) = await ValidateAsync(request, upload, existing, ct);
        if (errors.Count > 0)
        {
            return FieldErrors(errors);
        }

        if (await ReplacementConflictAsync(customId, targets, ct) is { } conflict)
        {
            return conflict;
        }

        var now = _time.GetUtcNow();
        var package = upload is not null && request.UploadId != customId;
        var record = existing with
        {
            Name = request.Name!.Trim(),
            Version = request.Version!.Trim(),
            Category = request.Category!.Trim(),
            Action = request.Action!.Value,
            BlobName = package ? upload!.BlobName : existing.BlobName,
            Sha256 = package ? upload!.Sha256! : existing.Sha256,
            Size = package ? upload!.SizeBytes : existing.Size,
            FileName = package ? upload!.FileName : existing.FileName,
            InfCount = package ? upload!.InfCount : existing.InfCount,
            InstallCommand = ParseCommand(request.InstallCommand),
            ChangedAt = now,
            ChangedBy = upn,
        };
        await _store.CustomComponents.UpsertAsync(DriverStore.CustomPartition, customId.ToString("N"), record, ct);
        if (package)
        {
            await _store.CustomUploads.UpsertAsync(DriverStore.UploadPartition, request.UploadId!.Value.ToString("N"), upload! with { CustomId = customId }, ct);
        }

        var before = (await _store.CustomTargets.ListPartitionAsync(customId.ToString("N"), ct)).ToList();
        await WriteTargetsAsync(customId, before, targets, ct);
        await _store.LogAsync(now, upn, request.Actor.ObjectId, "Custom component", record.Name, $"Changed, now on {Names(targets)}", ct);
        var affected = (await _models.ListAsync(ct)).Where(m => targets.Any(t => t.Model.ModelId == m.ModelId) || before.Any(b => b.RowKey == m.ModelId.ToString("D")));
        await MarkDirtyAsync(affected, now, ct);
        var models = (await _models.ListAsync(ct)).ToDictionary(m => m.ModelId);
        return DriverResult.Ok(await ToApiAsync(record, models, ct));
    }

    /// <summary>Removes a custom component; its models build without it and replaced vendor components return.</summary>
    public async Task<DriverResult> DeleteAsync(Guid customId, string? changedBy, CancellationToken ct = default)
    {
        if (changedBy is not { Length: > 0 })
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var key = customId.ToString("N");
        var existing = (await _store.CustomComponents.GetAsync(DriverStore.CustomPartition, key, ct))?.Value;
        if (existing is null)
        {
            return DriverResult.NotFound("Custom component not found.");
        }

        var before = (await _store.CustomTargets.ListPartitionAsync(key, ct)).ToList();
        await WriteTargetsAsync(customId, before, [], ct);
        await _store.CustomComponents.DeleteAsync(DriverStore.CustomPartition, key, ct: ct);

        // The package stays while a kept set lists it; clean-up deletes it after that.
        foreach (var upload in (await _store.CustomUploads.ListPartitionAsync(DriverStore.UploadPartition, ct)).Where(u => u.Value.CustomId == customId))
        {
            await _store.CustomUploads.DeleteAsync(DriverStore.UploadPartition, upload.RowKey, ct: ct);
        }

        var now = _time.GetUtcNow();
        await _store.LogAsync(now, changedBy, null, "Custom component", existing.Name, "Removed", ct);
        await MarkDirtyAsync((await _models.ListAsync(ct)).Where(m => before.Any(b => b.RowKey == m.ModelId.ToString("D"))), now, ct);
        return new DriverResult(HttpStatusCode.NoContent);
    }

    /// <summary>Hides the new-vendor-version banner of one model until the vendor publishes another version.</summary>
    public async Task<DriverResult> DismissAsync(Guid customId, Guid modelId, DriverActor? actor, CancellationToken ct = default)
    {
        if (actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var row = await _store.CustomTargets.GetAsync(customId.ToString("N"), modelId.ToString("D"), ct);
        if (row is null)
        {
            return DriverResult.NotFound("Custom component not found.");
        }

        await _store.CustomTargets.UpsertAsync(customId.ToString("N"), row.RowKey, row.Value with { DismissedVersion = row.Value.NoticeVersion, DismissedAt = _time.GetUtcNow(), DismissedBy = upn }, ct);
        return new DriverResult(HttpStatusCode.NoContent);
    }

    /// <summary>"setup.exe /quiet" as an executable and its arguments, or null.</summary>
    public static DriverInstallCommand? ParseCommand(string? command)
    {
        var text = command?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        string executable;
        string rest;
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            executable = end > 0 ? text[1..end] : text.Trim('"');
            rest = end > 0 ? text[(end + 1)..] : string.Empty;
        }
        else
        {
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            executable = space > 0 ? text[..space] : text;
            rest = space > 0 ? text[space..] : string.Empty;
        }

        return new DriverInstallCommand { Executable = executable, Arguments = rest.Trim() };
    }

    private static string? SafeFileName(string? name)
    {
        var file = Path.GetFileName(name?.Trim() ?? string.Empty);
        if (string.IsNullOrEmpty(file) || file.Length > 120)
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(file.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '-' : c).ToArray());
    }

    private static DriverResult FieldErrors(List<(string Field, string Detail)> errors) =>
        new(HttpStatusCode.BadRequest, Detail: errors[0].Detail, Extra: new Dictionary<string, object?>
        {
            ["errors"] = errors.Select(e => new { field = e.Field, detail = e.Detail }).ToList(),
        });

    private static string Names(IEnumerable<(DriverModelRecord Model, string? Replaces)> targets) =>
        string.Join(", ", targets.Select(t => t.Model.DisplayName));

    private async Task<(List<(string Field, string Detail)> Errors, List<(DriverModelRecord Model, string? Replaces)> Targets)> ValidateAsync(DriverCustomComponentRequest request, DriverCustomUploadRecord? upload, DriverCustomComponentRecord? existing, CancellationToken ct)
    {
        var errors = new List<(string, string)>();
        var newPackage = existing is null || (request.UploadId is { } uploadId && uploadId != existing.CustomId);
        if (newPackage && upload is not { Completed: true })
        {
            errors.Add(("uploadId", "Upload the package first."));
        }

        if (request.Name?.Trim() is not { Length: >= 1 and <= 100 })
        {
            errors.Add(("name", "Enter a name of 1 to 100 characters."));
        }

        if (request.Version?.Trim() is not { Length: >= 1 and <= 50 })
        {
            errors.Add(("version", "Enter a version of 1 to 50 characters."));
        }

        if (request.Category?.Trim() is not { Length: >= 1 and <= 100 })
        {
            errors.Add(("category", "Choose a category."));
        }

        if (request.Action is not { } action || action == DriverAction.Skip || !Enum.IsDefined(action))
        {
            errors.Add(("action", "Choose Offline, Offline + Specialize or Specialize."));
        }
        else
        {
            var fileName = upload?.FileName ?? existing?.FileName ?? string.Empty;
            var infCount = upload?.InfCount ?? existing?.InfCount ?? 0;
            if (action is DriverAction.Offline or DriverAction.OfflineAndSpecialize
                && (!fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || infCount < 1))
            {
                errors.Add(("uploadId", "For Offline, upload a ZIP with the driver's INF files."));
            }

            if (action is DriverAction.Specialize or DriverAction.OfflineAndSpecialize)
            {
                var command = ParseCommand(request.InstallCommand);
                if (command is null)
                {
                    errors.Add(("installCommand", "Enter the silent install command."));
                }
                // Checked as a Windows path whatever the host OS: no root, drive, UNC or parent segment.
                else if (command.Executable.StartsWith('\\') || command.Executable.StartsWith('/') || command.Executable.Contains("..", StringComparison.Ordinal) || command.Executable.Contains(':', StringComparison.Ordinal))
                {
                    errors.Add(("installCommand", "Use a path inside the package."));
                }
            }
        }

        var targets = new List<(DriverModelRecord, string?)>();
        var requested = request.Targets ?? [];
        if (requested.Count == 0)
        {
            errors.Add(("targets", "Choose at least one model."));
        }

        foreach (var target in requested.DistinctBy(t => t.ModelId))
        {
            var model = (await _models.GetAsync(target.ModelId, ct))?.Value;
            if (model is null)
            {
                errors.Add(("targets", "Choose onboarded models."));
                continue;
            }

            if (target.ReplacesLineageKey is { Length: > 0 } lineage)
            {
                var known = false;
                foreach (var release in model.Releases)
                {
                    known |= await _store.Components.GetAsync(DriverTables.Pair(model.ModelId, release), lineage, ct) is not null;
                }

                if (!known)
                {
                    errors.Add(("targets", $"{model.DisplayName} has no such vendor component."));
                    continue;
                }
            }

            targets.Add((model, string.IsNullOrEmpty(target.ReplacesLineageKey) ? null : target.ReplacesLineageKey));
        }

        return (errors, targets);
    }

    private async Task<DriverResult?> ReplacementConflictAsync(Guid customId, List<(DriverModelRecord Model, string? Replaces)> targets, CancellationToken ct)
    {
        foreach (var (model, lineage) in targets.Where(t => t.Replaces is not null))
        {
            var taken = await _store.Replacements.GetAsync(model.ModelId.ToString("D"), lineage!, ct);
            if (taken is not null && taken.Value.CustomId != customId)
            {
                var other = (await _store.CustomComponents.GetAsync(DriverStore.CustomPartition, taken.Value.CustomId.ToString("N"), ct))?.Value.Name ?? "Another custom component";
                return DriverResult.Conflict($"{other} already replaces this component on {model.DisplayName}.");
            }
        }

        return null;
    }

    // Replaces the targets and their replacement rows; a target that keeps its replacement keeps its notice.
    private async Task WriteTargetsAsync(Guid customId, IReadOnlyList<Stored<DriverCustomTargetRecord>> before, IReadOnlyList<(DriverModelRecord Model, string? Replaces)> after, CancellationToken ct)
    {
        var key = customId.ToString("N");
        foreach (var old in before)
        {
            if (old.Value.ReplacesLineageKey is { } lineage)
            {
                await _store.Replacements.DeleteAsync(old.RowKey, lineage, ct: ct);
            }

            if (!after.Any(t => t.Model.ModelId.ToString("D") == old.RowKey))
            {
                await _store.CustomTargets.DeleteAsync(key, old.RowKey, ct: ct);
            }
        }

        foreach (var (model, lineage) in after)
        {
            var modelKey = model.ModelId.ToString("D");
            var kept = before.FirstOrDefault(b => b.RowKey == modelKey)?.Value;
            var record = kept is not null && kept.ReplacesLineageKey == lineage ? kept : new DriverCustomTargetRecord { ReplacesLineageKey = lineage };
            await _store.CustomTargets.UpsertAsync(key, modelKey, record, ct);
            if (lineage is not null)
            {
                await _store.Replacements.UpsertAsync(modelKey, lineage, new DriverReplacementRecord { CustomId = customId }, ct);
            }
        }
    }

    private async Task MarkDirtyAsync(IEnumerable<DriverModelRecord> models, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var model in models)
        {
            foreach (var release in model.Releases)
            {
                await _policy.MarkDirtyAsync(model, release, now, ct);
            }
        }
    }

    private async Task<DriverCustomComponent> ToApiAsync(DriverCustomComponentRecord record, Dictionary<Guid, DriverModelRecord> models, CancellationToken ct)
    {
        var targets = new List<DriverCustomTarget>();
        foreach (var row in await _store.CustomTargets.ListPartitionAsync(record.CustomId.ToString("N"), ct))
        {
            if (!Guid.TryParse(row.RowKey, out var modelId))
            {
                continue;
            }

            var model = models.GetValueOrDefault(modelId);
            string? replacesName = null;
            if (model is not null && row.Value.ReplacesLineageKey is { } lineage)
            {
                foreach (var release in model.Releases)
                {
                    replacesName ??= (await _store.Components.GetAsync(DriverTables.Pair(modelId, release), lineage, ct))?.Value.Name;
                }
            }

            targets.Add(new DriverCustomTarget { ModelId = modelId, ModelName = model?.DisplayName, ReplacesLineageKey = row.Value.ReplacesLineageKey, ReplacesName = replacesName });
        }

        return new DriverCustomComponent
        {
            CustomId = record.CustomId,
            Name = record.Name,
            Version = record.Version,
            Category = record.Category,
            Action = record.Action,
            FileName = record.FileName,
            SizeBytes = record.Size,
            InfCount = record.InfCount,
            InstallCommand = record.InstallCommand is { } command ? $"{command.Executable} {command.Arguments}".Trim() : null,
            UploadedBy = record.UploadedBy,
            UploadedAt = record.UploadedAt,
            Targets = targets,
        };
    }
}
