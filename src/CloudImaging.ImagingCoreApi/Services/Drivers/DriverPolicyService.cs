using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// Rules, overrides, critical categories and Build now (FR-019 to FR-024, FR-063). A policy change
/// marks the affected active pairs dirty: their set is built 2 minutes after the last change by a
/// delayed <c>BuildSet</c> message, so several changes make one set. Drafts are never built.
/// </summary>
public sealed class DriverPolicyService
{
    /// <summary>Edits made within this time of each other make one set.</summary>
    public static readonly TimeSpan BuildDelay = TimeSpan.FromMinutes(2);

    private const int MaxNameLength = 100;
    private const int MaxValueLength = 200;
    private const int MaxComponentIdLength = 80;
    private const int MaxConcurrencyRetries = 20;

    // FR-023: offered, never inserted without an administrator adding them.
    private static readonly (string Name, string Pattern)[] Suggested =
    [
        ("HP Services Scan for Commercial", "HP Services Scan for Commercial*"),
        ("Seamless Firmware Update Service", "Seamless Firmware Update Service*"),
        ("HP App", "HP App"),
    ];

    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverSetPointerRepository _pointers;
    private readonly DriverModelService _modelService;
    private readonly IDriverWorkSender _work;
    private readonly TimeProvider _time;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="pointers">Set pointers.</param>
    /// <param name="modelService">Resolution and component rows.</param>
    /// <param name="work">Queue.</param>
    /// <param name="time">Clock.</param>
    public DriverPolicyService(DriverStore store, DriverModelRepository models, DriverSetPointerRepository pointers, DriverModelService modelService, IDriverWorkSender work, TimeProvider time)
    {
        _store = store;
        _models = models;
        _pointers = pointers;
        _modelService = modelService;
        _work = work;
        _time = time;
    }

    /// <summary>Every rule by name, with the number of components it decides.</summary>
    public async Task<IReadOnlyList<DriverRule>> ListRulesAsync(CancellationToken ct = default)
    {
        var rules = await _modelService.RulesAsync(ct);
        var hits = await HitsAsync(rules, ct);
        return rules
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => ToContract(r, hits.GetValueOrDefault(r.RuleId)))
            .ToList();
    }

    /// <summary>Suggested rules that do not exist yet, with the components each would decide.</summary>
    public async Task<IReadOnlyList<DriverRule>> SuggestionsAsync(CancellationToken ct = default)
    {
        var rules = await _modelService.RulesAsync(ct);
        var suggestions = Suggested
            .Where(s => !rules.Any(r => string.Equals(r.Name, s.Name, StringComparison.OrdinalIgnoreCase)
                || (r.Match == DriverRuleMatch.NamePattern && string.Equals(r.Value, s.Pattern, StringComparison.OrdinalIgnoreCase))))
            .Select(s => new DriverRuleRecord { RuleId = Guid.NewGuid(), Name = s.Name, Scope = DriverRuleScope.Hp, Match = DriverRuleMatch.NamePattern, Value = s.Pattern, Action = DriverAction.Skip })
            .ToList();

        // Hits are counted as if the suggestion were added on top of the existing rules.
        var result = new List<DriverRule>();
        foreach (var suggestion in suggestions)
        {
            var hits = await HitsAsync([.. rules, suggestion with { ChangedAt = DateTimeOffset.MaxValue }], ct);
            result.Add(ToContract(suggestion, hits.GetValueOrDefault(suggestion.RuleId)) with { RuleId = Guid.Empty });
        }

        return result;
    }

    /// <summary>Creates a rule; 400 with field errors, 409 for a name in use.</summary>
    public async Task<DriverResult> CreateRuleAsync(DriverRuleRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var rules = await _modelService.RulesAsync(ct);
        if (Validate(request, rules, null) is { } invalid)
        {
            return invalid;
        }

        var now = _time.GetUtcNow();
        var rule = new DriverRuleRecord
        {
            RuleId = Guid.NewGuid(),
            Name = request.Name!.Trim(),
            Scope = request.Scope,
            Match = request.Match,
            Value = request.Value!.Trim(),
            Action = request.Action,
            ChangedAt = now,
            ChangedBy = upn,
        };
        await _store.Rules.UpsertAsync(DriverStore.RulePartition, rule.RuleId.ToString("D"), rule, ct);
        await _store.LogAsync(now, upn, request.Actor.ObjectId, "Rule", rule.Name, $"Added: {Describe(rule)}", ct);
        await MarkScopeDirtyAsync([rule.Scope], now, ct);
        var hits = await HitsAsync([.. rules, rule], ct);
        return new DriverResult(HttpStatusCode.Created, ToContract(rule, hits.GetValueOrDefault(rule.RuleId)));
    }

    /// <summary>Replaces a rule; 404 when unknown.</summary>
    public async Task<DriverResult> UpdateRuleAsync(Guid ruleId, DriverRuleRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var existing = (await _store.Rules.GetAsync(DriverStore.RulePartition, ruleId.ToString("D"), ct))?.Value;
        if (existing is null)
        {
            return DriverResult.NotFound("Rule not found.");
        }

        var rules = await _modelService.RulesAsync(ct);
        if (Validate(request, rules, ruleId) is { } invalid)
        {
            return invalid;
        }

        var now = _time.GetUtcNow();
        var rule = existing with
        {
            Name = request.Name!.Trim(),
            Scope = request.Scope,
            Match = request.Match,
            Value = request.Value!.Trim(),
            Action = request.Action,
            ChangedAt = now,
            ChangedBy = upn,
        };
        await _store.Rules.UpsertAsync(DriverStore.RulePartition, ruleId.ToString("D"), rule, ct);
        await _store.LogAsync(now, upn, request.Actor.ObjectId, "Rule", rule.Name, $"Changed from {Describe(existing)} to {Describe(rule)}", ct);
        await MarkScopeDirtyAsync([existing.Scope, rule.Scope], now, ct);
        var hits = await HitsAsync([.. rules.Where(r => r.RuleId != ruleId), rule], ct);
        return DriverResult.Ok(ToContract(rule, hits.GetValueOrDefault(ruleId)));
    }

    /// <summary>Deletes a rule; 404 when unknown.</summary>
    public async Task<DriverResult> DeleteRuleAsync(Guid ruleId, string? changedBy, CancellationToken ct = default)
    {
        if (changedBy is not { Length: > 0 })
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var existing = (await _store.Rules.GetAsync(DriverStore.RulePartition, ruleId.ToString("D"), ct))?.Value;
        if (existing is null)
        {
            return DriverResult.NotFound("Rule not found.");
        }

        var now = _time.GetUtcNow();
        await _store.Rules.DeleteAsync(DriverStore.RulePartition, ruleId.ToString("D"), ct: ct);
        await _store.LogAsync(now, changedBy, null, "Rule", existing.Name, $"Deleted: {Describe(existing)}", ct);
        await MarkScopeDirtyAsync([existing.Scope], now, ct);
        return new DriverResult(HttpStatusCode.NoContent);
    }

    /// <summary>Sets a model's action for one component; 400 when the action is not allowed.</summary>
    public async Task<DriverResult> SetOverrideAsync(Guid modelId, WindowsRelease release, string lineageKey, DriverOverrideRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var (model, component, missing) = await FindComponentAsync(modelId, release, lineageKey, ct);
        if (missing is not null)
        {
            return missing;
        }

        if (component!.AllowedActions.Count <= 1)
        {
            return DriverResult.BadRequest("The vendor's data only allows Skip for this component.");
        }

        if (!Enum.IsDefined(request.Action) || !component.AllowedActions.Contains(request.Action))
        {
            return DriverResult.BadRequest($"{ActionLabel(request.Action)} is not allowed for this component.");
        }

        var now = _time.GetUtcNow();
        var pair = DriverTables.Pair(modelId, release);
        await _store.Overrides.UpsertAsync(pair, lineageKey, new DriverOverrideRecord { Action = request.Action, ChangedAt = now, ChangedBy = upn }, ct);
        await _store.LogAsync(now, upn, request.Actor.ObjectId, "Override", $"{model!.DisplayName}: {component.Name}", $"{ActionLabel(request.Action)} for {DriverModelService.ReleaseLabel(release)}", ct);
        await MarkDirtyAsync(model, release, now, ct);
        return DriverResult.Ok((await _modelService.GetComponentRowAsync(model, release, lineageKey, ct))!);
    }

    /// <summary>Removes a model's action for one component; 404 when there is none.</summary>
    public async Task<DriverResult> ClearOverrideAsync(Guid modelId, WindowsRelease release, string lineageKey, string? changedBy, CancellationToken ct = default)
    {
        if (changedBy is not { Length: > 0 })
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var (model, component, missing) = await FindComponentAsync(modelId, release, lineageKey, ct);
        if (missing is not null)
        {
            return missing;
        }

        var pair = DriverTables.Pair(modelId, release);
        if (!await _store.Overrides.DeleteAsync(pair, lineageKey, ct: ct))
        {
            return DriverResult.NotFound("This component has no override.");
        }

        var now = _time.GetUtcNow();
        await _store.LogAsync(now, changedBy, null, "Override", $"{model!.DisplayName}: {component!.Name}", $"Override undone for {DriverModelService.ReleaseLabel(release)}", ct);
        await MarkDirtyAsync(model, release, now, ct);
        return DriverResult.Ok((await _modelService.GetComponentRowAsync(model, release, lineageKey, ct))!);
    }

    /// <summary>Categories seen in components, per vendor, with their critical flag.</summary>
    public async Task<IReadOnlyList<DriverCategory>> ListCategoriesAsync(DriverVendor? vendor, CancellationToken ct = default)
    {
        var models = (await _models.ListAsync(ct)).Where(m => vendor is null || m.Vendor == vendor).ToList();
        var seen = new SortedSet<(DriverVendor, string)>(Comparer<(DriverVendor Vendor, string Category)>.Create((a, b) =>
        {
            var byVendor = a.Vendor.CompareTo(b.Vendor);
            return byVendor != 0 ? byVendor : StringComparer.OrdinalIgnoreCase.Compare(a.Category, b.Category);
        }));
        foreach (var model in models)
        {
            foreach (var release in model.Releases)
            {
                foreach (var row in await _store.Components.ListPartitionAsync(DriverTables.Pair(model.ModelId, release), ct))
                {
                    if (row.Value.Category is { Length: > 0 } category)
                    {
                        seen.Add((model.Vendor, category.Trim()));
                    }
                }
            }
        }

        var critical = new Dictionary<DriverVendor, HashSet<string>>();
        foreach (var v in Enum.GetValues<DriverVendor>().Where(v => vendor is null || v == vendor))
        {
            var names = await CriticalAsync(v, ct);
            critical[v] = names;

            // A critical category no current component has any more is still listed, so it can be cleared.
            foreach (var name in names)
            {
                seen.Add((v, name));
            }
        }

        return seen.Select(s => new DriverCategory { Vendor = s.Item1, Category = s.Item2, Critical = critical.TryGetValue(s.Item1, out var set) && set.Contains(s.Item2) }).ToList();
    }

    /// <summary>Replaces a vendor's critical categories. Nothing is rebuilt: the flag is read when a session is assigned.</summary>
    public async Task<DriverResult> SetCriticalCategoriesAsync(DriverVendor vendor, DriverCriticalCategoriesRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        if (vendor == DriverVendor.Lenovo || !Enum.IsDefined(vendor))
        {
            return DriverResult.BadRequest($"{DriverSyncService.VendorName(vendor)} is not supported.");
        }

        var wanted = request.Categories.Select(c => c?.Trim() ?? string.Empty).ToList();
        if (wanted.Any(c => c.Length is 0 or > MaxValueLength))
        {
            return DriverResult.BadRequest("Each category must be 1 to 200 characters.");
        }

        var now = _time.GetUtcNow();
        var current = await CriticalAsync(vendor, ct);
        var next = wanted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var partition = vendor.ToString();
        foreach (var row in await _store.CriticalCategories.ListPartitionAsync(partition, ct))
        {
            if (!next.Contains(row.Value.Category ?? row.RowKey))
            {
                await _store.CriticalCategories.DeleteAsync(partition, row.RowKey, ct: ct);
            }
        }

        foreach (var category in next.Where(c => !current.Contains(c)))
        {
            await _store.CriticalCategories.UpsertAsync(partition, DriverTables.CategoryKey(category), new DriverCriticalCategoryRecord { Category = category, ChangedAt = now, ChangedBy = upn }, ct);
        }

        if (!current.SetEquals(next))
        {
            var change = next.Count == 0 ? "None" : string.Join(", ", next.Order(StringComparer.OrdinalIgnoreCase));
            await _store.LogAsync(now, upn, request.Actor.ObjectId, "CriticalCategory", DriverSyncService.VendorName(vendor), change, ct);
        }

        return DriverResult.Ok(await ListCategoriesAsync(vendor, ct));
    }

    /// <summary>
    /// Approves components waiting for approval (FR-026): each becomes active with its current
    /// action, or with the chosen one (Skip included) as an override, and the set is built again.
    /// 409 when a component is not waiting; nothing is changed then.
    /// </summary>
    public async Task<DriverResult> ApproveAsync(Guid modelId, WindowsRelease release, DriverApprovalRequest? request, CancellationToken ct = default)
    {
        if (request is null)
        {
            return DriverResult.BadRequest("The request body is not valid.");
        }

        if (request.Actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        // JSON null overrides the initializers, and keys reach table lookups, so both are checked here.
        var approvals = (request.LineageKeys ?? []).Select(key => new DriverApproval { LineageKey = key })
            .Concat(request.Approvals ?? [])
            .DistinctBy(a => a.LineageKey, StringComparer.Ordinal)
            .ToList();
        if (approvals.Any(a => string.IsNullOrEmpty(a?.LineageKey) || a.LineageKey.Length > 80 || !a.LineageKey.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            return DriverResult.BadRequest("A component key is not valid.");
        }

        if (approvals.Count == 0)
        {
            return DriverResult.BadRequest("Choose at least one component to approve.");
        }

        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null || !model.Releases.Contains(release))
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        // Every component is checked before anything is written, so a bad request changes nothing.
        var pair = DriverTables.Pair(modelId, release);
        var rows = new List<(DriverApproval Approval, DriverComponentRecord Component)>();
        foreach (var approval in approvals)
        {
            var component = (await _store.Components.GetAsync(pair, approval.LineageKey, ct))?.Value;
            if (component is null)
            {
                return DriverResult.NotFound("Component not found.");
            }

            if (component.State != DriverComponentState.WaitingForApproval)
            {
                return DriverResult.Conflict($"{component.Name} is not waiting for approval.");
            }

            if (approval.Action is { } chosen && (!Enum.IsDefined(chosen) || !component.AllowedActions.Contains(chosen)))
            {
                return DriverResult.BadRequest($"{ActionLabel(chosen)} is not allowed for {component.Name}.");
            }

            rows.Add((approval, component));
        }

        var now = _time.GetUtcNow();
        var rules = await _modelService.RulesAsync(ct);
        var overrides = (await _store.Overrides.ListPartitionAsync(pair, ct)).ToDictionary(r => r.RowKey, r => r.Value.Action, StringComparer.Ordinal);
        foreach (var (approval, component) in rows)
        {
            var current = DriverPolicyResolver.Resolve(new DriverPolicyInput(model.Vendor, component.ComponentId, component.Name, component.Category, component.AllowedActions, component.DefaultAction), overrides.TryGetValue(approval.LineageKey, out var o) ? o : null, rules).Action;
            var action = approval.Action ?? current;
            if (action != current)
            {
                await _store.Overrides.UpsertAsync(pair, approval.LineageKey, new DriverOverrideRecord { Action = action, ChangedAt = now, ChangedBy = upn }, ct);
            }

            await _store.Components.UpsertAsync(pair, approval.LineageKey, component with { State = DriverComponentState.Active }, ct);
            await _store.LogAsync(now, upn, request.Actor.ObjectId, "Approval", $"{model.DisplayName}: {component.Name}", $"Approved as {ActionLabel(action)} for {DriverModelService.ReleaseLabel(release)}", ct);
        }

        await MarkDirtyAsync(model, release, now, ct);
        return await _modelService.GetModelPageAsync(modelId, release, ct);
    }

    /// <summary>Builds a pair's waiting changes at once; 409 when nothing is waiting.</summary>
    public async Task<DriverResult> BuildNowAsync(Guid modelId, WindowsRelease release, DriverActor? actor, CancellationToken ct = default)
    {
        if (actor?.Upn is not { Length: > 0 } upn)
        {
            return DriverResult.BadRequest(DriverHttpText.MissingActor);
        }

        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null || !model.Releases.Contains(release))
        {
            return DriverResult.NotFound("Driver model not found.");
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _pointers.GetAsync(modelId, release, ct);
            if (row?.Value is not { Dirty: true } pointer || model.State != DriverModelState.Active)
            {
                return DriverResult.Conflict("No changes are waiting.");
            }

            if (await _pointers.TryUpdateAsync(modelId, release, pointer with { BuildAfter = null }, row.ETag, ct))
            {
                await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.BuildSet, ModelId = modelId, Release = release }, TimeSpan.Zero, ct);
                await _store.LogAsync(_time.GetUtcNow(), upn, actor.ObjectId, "Model", model.DisplayName, $"Build now for {DriverModelService.ReleaseLabel(release)}", ct);
                return new DriverResult(HttpStatusCode.Accepted);
            }
        }

        return DriverResult.Conflict("The driver set changed while the build was being started. Try again.");
    }

    /// <summary>
    /// Marks an active pair dirty: <c>BuildAfter</c> moves to 2 minutes from now, the change is
    /// counted for the banner, and a <c>BuildSet</c> is queued for then. Drafts are left alone.
    /// </summary>
    public async Task MarkDirtyAsync(DriverModelRecord model, WindowsRelease release, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.State != DriverModelState.Active)
        {
            return;
        }

        for (var attempt = 0; attempt < MaxConcurrencyRetries; attempt++)
        {
            var row = await _pointers.GetOrCreateAsync(model.ModelId, release, ct);
            var next = row.Value with { Dirty = true, BuildAfter = now + BuildDelay, PendingChanges = row.Value.PendingChanges + 1 };
            if (await _pointers.TryUpdateAsync(model.ModelId, release, next, row.ETag, ct))
            {
                await _work.EnqueueAsync(new DriverWorkMessage { Kind = DriverWorkKind.BuildSet, ModelId = model.ModelId, Release = release }, BuildDelay, ct);
                return;
            }
        }

        throw new InvalidOperationException($"The driver set pointer of {DriverTables.Pair(model.ModelId, release)} kept changing.");
    }

    /// <summary>Label of an action, as the portal shows it.</summary>
    public static string ActionLabel(DriverAction action) => action switch
    {
        DriverAction.OfflineAndSpecialize => "Offline + Specialize",
        _ => action.ToString(),
    };

    private async Task MarkScopeDirtyAsync(IEnumerable<DriverRuleScope> scopes, DateTimeOffset now, CancellationToken ct)
    {
        var scopeSet = scopes.ToHashSet();
        foreach (var model in await _models.ListAsync(ct))
        {
            var inScope = scopeSet.Contains(DriverRuleScope.AllVendors)
                || (model.Vendor == DriverVendor.Hp && scopeSet.Contains(DriverRuleScope.Hp))
                || (model.Vendor == DriverVendor.Dell && scopeSet.Contains(DriverRuleScope.Dell));
            if (!inScope)
            {
                continue;
            }

            foreach (var release in model.Releases)
            {
                await MarkDirtyAsync(model, release, now, ct);
            }
        }
    }

    private async Task<(DriverModelRecord? Model, DriverComponentRecord? Component, DriverResult? Missing)> FindComponentAsync(Guid modelId, WindowsRelease release, string lineageKey, CancellationToken ct)
    {
        var model = (await _models.GetAsync(modelId, ct))?.Value;
        if (model is null || !model.Releases.Contains(release))
        {
            return (null, null, DriverResult.NotFound("Driver model not found."));
        }

        var component = (await _store.Components.GetAsync(DriverTables.Pair(modelId, release), lineageKey, ct))?.Value;
        return component is null
            ? (model, null, DriverResult.NotFound("Component not found."))
            : (model, component, null);
    }

    // Hits count the components a rule decides, drafts included, so a rule can be checked before onboarding is completed.
    private async Task<Dictionary<Guid, int>> HitsAsync(IReadOnlyList<DriverRuleRecord> rules, CancellationToken ct)
    {
        var hits = new Dictionary<Guid, int>();
        foreach (var model in await _models.ListAsync(ct))
        {
            foreach (var release in model.Releases)
            {
                foreach (var resolved in await _modelService.ResolveAsync(model, release, rules, ct))
                {
                    if (resolved.Component.State != DriverComponentState.WaitingForApproval && resolved.Decision.RuleId is { } ruleId)
                    {
                        hits[ruleId] = hits.GetValueOrDefault(ruleId) + 1;
                    }
                }
            }
        }

        return hits;
    }

    private async Task<HashSet<string>> CriticalAsync(DriverVendor vendor, CancellationToken ct) =>
        (await _store.CriticalCategories.ListPartitionAsync(vendor.ToString(), ct))
            .Select(r => r.Value.Category ?? Uri.UnescapeDataString(r.RowKey))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static DriverResult? Validate(DriverRuleRequest request, IReadOnlyList<DriverRuleRecord> rules, Guid? ruleId)
    {
        var errors = new List<(string Field, string Detail)>();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            errors.Add(("name", "Enter a name of 1 to 100 characters."));
        }

        if (!Enum.IsDefined(request.Scope))
        {
            errors.Add(("scope", "Choose which vendors the rule applies to."));
        }

        if (!Enum.IsDefined(request.Action))
        {
            errors.Add(("action", "Choose an action."));
        }

        var value = request.Value?.Trim() ?? string.Empty;
        switch (request.Match)
        {
            case DriverRuleMatch.Category when value.Length is 0 or > MaxValueLength:
                errors.Add(("value", "Choose a category."));
                break;
            case DriverRuleMatch.NamePattern when value.Length is 0 or > MaxValueLength:
                errors.Add(("value", "Enter a pattern of 1 to 200 characters. Use * as a wildcard."));
                break;
            case DriverRuleMatch.NamePattern when value.Contains('?', StringComparison.Ordinal):
                errors.Add(("value", "Use * as the only wildcard."));
                break;
            case DriverRuleMatch.ComponentId when value.Length is 0 or > MaxComponentIdLength || value.Contains('*', StringComparison.Ordinal):
                errors.Add(("value", "Enter a component ID of 1 to 80 characters, without wildcards."));
                break;
            case DriverRuleMatch.Category or DriverRuleMatch.NamePattern or DriverRuleMatch.ComponentId:
                break;
            default:
                errors.Add(("match", "Choose what the rule matches."));
                break;
        }

        if (errors.Count > 0)
        {
            return new DriverResult(HttpStatusCode.BadRequest, Detail: errors[0].Detail, Extra: new Dictionary<string, object?>
            {
                ["errors"] = errors.Select(e => new { field = e.Field, detail = e.Detail }).ToList(),
            });
        }

        return rules.Any(r => r.RuleId != ruleId && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            ? DriverResult.Conflict($"A rule named \"{name}\" already exists.")
            : null;
    }

    private static string Describe(DriverRuleRecord rule)
    {
        var scope = rule.Scope switch
        {
            DriverRuleScope.Hp => "HP",
            DriverRuleScope.Dell => "Dell",
            _ => "all vendors",
        };
        var match = rule.Match switch
        {
            DriverRuleMatch.Category => "category",
            DriverRuleMatch.NamePattern => "name",
            _ => "ID",
        };
        return $"{ActionLabel(rule.Action)} when {match} is {rule.Value}, {scope}";
    }

    private static DriverRule ToContract(DriverRuleRecord rule, int hits) => new()
    {
        RuleId = rule.RuleId,
        Name = rule.Name,
        Scope = rule.Scope,
        Match = rule.Match,
        Value = rule.Value,
        Action = rule.Action,
        ChangedAt = rule.ChangedAt,
        ChangedBy = rule.ChangedBy,
        Hits = hits,
    };
}
