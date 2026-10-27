using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>What the policy needs to know about one component.</summary>
/// <param name="Vendor">Vendor of the model.</param>
/// <param name="ComponentId">Vendor component id.</param>
/// <param name="Name">Component name.</param>
/// <param name="Category">Vendor category or type.</param>
/// <param name="AllowedActions">Actions the vendor data allows.</param>
/// <param name="DefaultAction">Vendor default.</param>
public sealed record DriverPolicyInput(
    DriverVendor Vendor,
    string ComponentId,
    string Name,
    string? Category,
    IReadOnlyList<DriverAction> AllowedActions,
    DriverAction DefaultAction);

/// <summary>The resolved action and where it came from.</summary>
/// <param name="Action">Action.</param>
/// <param name="Source">Source.</param>
/// <param name="RuleName">Rule name when a rule decided.</param>
/// <param name="RuleId">Rule id when a rule decided.</param>
public sealed record DriverPolicyDecision(DriverAction Action, DriverActionSource Source, string? RuleName = null, Guid? RuleId = null);

/// <summary>
/// Resolves a component's action (FR-019 to FR-022): Locked when only one action is allowed;
/// otherwise the model's override, then rules for the component's vendor, then rules for all
/// vendors, then the vendor default. Within a layer a component-id rule beats a name pattern beats
/// a category, and among rules of the same kind the most recently changed one wins. A candidate
/// whose action is not allowed for the component is ignored and the next one decides.
/// </summary>
public static class DriverPolicyResolver
{
    /// <summary>Resolves one component.</summary>
    public static DriverPolicyDecision Resolve(DriverPolicyInput component, DriverAction? overrideAction, IReadOnlyList<DriverRuleRecord> rules)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(rules);

        var allowed = component.AllowedActions.Count == 0 ? [DriverAction.Skip] : component.AllowedActions;
        if (allowed.Count == 1)
        {
            return new DriverPolicyDecision(allowed[0], DriverActionSource.Locked);
        }

        if (overrideAction is { } chosen && allowed.Contains(chosen))
        {
            return new DriverPolicyDecision(chosen, DriverActionSource.Override);
        }

        var vendorScope = component.Vendor switch
        {
            DriverVendor.Hp => DriverRuleScope.Hp,
            DriverVendor.Dell => DriverRuleScope.Dell,
            _ => (DriverRuleScope?)null,
        };

        var candidates = rules
            .Where(rule => (rule.Scope == vendorScope || rule.Scope == DriverRuleScope.AllVendors) && Matches(rule, component))
            .OrderBy(rule => rule.Scope == DriverRuleScope.AllVendors ? 1 : 0)
            .ThenBy(rule => Specificity(rule.Match))
            .ThenByDescending(rule => rule.ChangedAt);
        foreach (var rule in candidates)
        {
            if (allowed.Contains(rule.Action))
            {
                return new DriverPolicyDecision(rule.Action, DriverActionSource.Rule, rule.Name, rule.RuleId);
            }
        }

        var fallback = allowed.Contains(component.DefaultAction) ? component.DefaultAction : allowed[0];
        return new DriverPolicyDecision(fallback, DriverActionSource.Default);
    }

    /// <summary>Whether a rule matches a component, ignoring scope.</summary>
    public static bool Matches(DriverRuleRecord rule, DriverPolicyInput component)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(component);
        return rule.Match switch
        {
            DriverRuleMatch.ComponentId => string.Equals(rule.Value.Trim(), component.ComponentId, StringComparison.OrdinalIgnoreCase),
            DriverRuleMatch.NamePattern => MatchesNamePattern(rule.Value, component.Name),
            DriverRuleMatch.Category => string.Equals(rule.Value.Trim(), component.Category?.Trim(), StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// Case-insensitive match where <c>*</c> stands for any text and every other character is
    /// literal. The literal pieces are found left to right, so the time is bounded by the name's
    /// length per piece and a long name cannot make it backtrack.
    /// </summary>
    public static bool MatchesNamePattern(string pattern, string name)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(name);

        var pieces = pattern.Split('*');
        if (pieces.Length == 1)
        {
            return string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase);
        }

        var first = pieces[0];
        var last = pieces[^1];
        if (first.Length + last.Length > name.Length
            || !name.StartsWith(first, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(last, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var position = first.Length;
        var end = name.Length - last.Length;
        for (var i = 1; i < pieces.Length - 1; i++)
        {
            var piece = pieces[i];
            if (piece.Length == 0)
            {
                continue;
            }

            var found = name.IndexOf(piece, position, end - position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return false;
            }

            position = found + piece.Length;
        }

        return true;
    }

    private static int Specificity(DriverRuleMatch match) => match switch
    {
        DriverRuleMatch.ComponentId => 0,
        DriverRuleMatch.NamePattern => 1,
        _ => 2,
    };
}
