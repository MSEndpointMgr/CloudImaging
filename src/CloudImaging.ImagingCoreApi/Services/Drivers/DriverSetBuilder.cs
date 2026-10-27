using System.Security.Cryptography;
using System.Text;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>One component's file as a set would use it, with its storage status.</summary>
/// <param name="Item">The set item.</param>
/// <param name="Status">Whether its file is verified, still downloading, or failed.</param>
public sealed record DriverSetFileCandidate(DriverSetItem Item, DriverFileStatus Status);

/// <summary>
/// One included component as policy resolved it. <see cref="Desired"/> is the version the set
/// should use (the vendor's latest, a rolled-back version, or a custom package); null for a
/// withdrawn component. <see cref="LastVerified"/> is the last version whose file passed
/// verification, used when the desired file failed or the component was withdrawn (FR-031).
/// </summary>
/// <param name="LineageKey">Lineage key.</param>
/// <param name="Action">Resolved action; Skip components are never candidates.</param>
/// <param name="Desired">Desired file.</param>
/// <param name="LastVerified">Fallback.</param>
public sealed record DriverSetCandidate(string LineageKey, DriverAction Action, DriverSetFileCandidate? Desired, DriverSetItem? LastVerified);

/// <summary>What a build would produce.</summary>
/// <param name="Items">Items in set order.</param>
/// <param name="Fingerprint">Fingerprint of the items.</param>
/// <param name="Ready">True when every item's file is verified.</param>
/// <param name="WaitingFileKeys">Files still downloading.</param>
/// <param name="FellBack">Lineages that kept their last verified version or were left out.</param>
public sealed record DriverSetBuildPlan(
    IReadOnlyList<DriverSetItem> Items,
    string Fingerprint,
    bool Ready,
    IReadOnlyList<string> WaitingFileKeys,
    IReadOnlyList<string> FellBack);

/// <summary>What happens to the set pointer.</summary>
public enum DriverSetDecisionKind
{
    /// <summary>Same fingerprint as the current set, or as the set rolled back from: no new set.</summary>
    NoChange,

    /// <summary>A new set waits for its files.</summary>
    Pending,

    /// <summary>A new set becomes current.</summary>
    NewCurrent,
}

/// <summary>The pointer after a build.</summary>
/// <param name="Kind">Outcome.</param>
/// <param name="Next">New pointer value (unchanged for <see cref="DriverSetDecisionKind.NoChange"/>).</param>
/// <param name="Version">Version of the pending or new set.</param>
public sealed record DriverSetDecision(DriverSetDecisionKind Kind, DriverSetPointerRecord Next, int? Version);

/// <summary>
/// Builds driver sets (FR-028 to FR-031): which items a set holds, its fingerprint, and how the
/// set pointer moves. Pure; the caller reads the inputs and writes the results with the pointer's
/// ETag. Skip components are not part of a set or its fingerprint, and a set carries no critical
/// flag (critical categories are looked up at assignment).
/// </summary>
public static class DriverSetBuilder
{
    /// <summary>Plans a set from the included components.</summary>
    public static DriverSetBuildPlan Plan(IEnumerable<DriverSetCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var items = new List<DriverSetItem>();
        var waiting = new List<string>();
        var fellBack = new List<string>();
        foreach (var candidate in candidates.Where(c => c.Action != DriverAction.Skip))
        {
            var desired = candidate.Desired;
            if (desired is { Status: DriverFileStatus.Verified or DriverFileStatus.Downloading })
            {
                items.Add(desired.Item with { Action = candidate.Action });
                if (desired.Status == DriverFileStatus.Downloading)
                {
                    waiting.Add(desired.Item.FileKey);
                }

                continue;
            }

            // A failed download (or a withdrawn component) keeps the last verified version, or
            // leaves the component out when there never was one.
            fellBack.Add(candidate.LineageKey);
            if (candidate.LastVerified is { } fallback)
            {
                items.Add(fallback with { Action = candidate.Action });
            }
        }

        var ordered = Order(items);
        return new DriverSetBuildPlan(ordered, Fingerprint(ordered), waiting.Count == 0, waiting, fellBack);
    }

    /// <summary>Set order: Offline injection first, then category and name.</summary>
    public static IReadOnlyList<DriverSetItem> Order(IEnumerable<DriverSetItem> items) =>
        items.OrderBy(item => item.Action == DriverAction.Specialize ? 1 : 0)
            .ThenBy(item => item.Category ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.LineageKey, StringComparer.Ordinal)
            .ToList();

    /// <summary>Fingerprint over lineage, component, version, action, origin and file hash, independent of order.</summary>
    public static string Fingerprint(IEnumerable<DriverSetItem> items)
    {
        var lines = items
            .Select(item => string.Join('|', item.LineageKey, item.ComponentId, item.Version, item.Action, item.Origin, item.Sha256.ToLowerInvariant()))
            .Order(StringComparer.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    /// <summary>Decides how the pointer moves for a plan.</summary>
    public static DriverSetDecision Decide(DriverSetPointerRecord current, DriverSetBuildPlan plan, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Fingerprint == current.CurrentFingerprint || plan.Fingerprint == current.RolledBackFromFingerprint)
        {
            return new DriverSetDecision(DriverSetDecisionKind.NoChange, current with { PendingVersion = null }, null);
        }

        var version = current.PendingVersion ?? current.LastVersion + 1;
        if (!plan.Ready)
        {
            return new DriverSetDecision(
                DriverSetDecisionKind.Pending,
                current with { PendingVersion = version, LastVersion = Math.Max(current.LastVersion, version) },
                version);
        }

        return new DriverSetDecision(
            DriverSetDecisionKind.NewCurrent,
            current with
            {
                PreviousVersion = current.CurrentVersion ?? current.PreviousVersion,
                CurrentVersion = version,
                CurrentFingerprint = plan.Fingerprint,
                RolledBackFromFingerprint = null,
                LastVersion = Math.Max(current.LastVersion, version),
                PendingVersion = null,
                BuiltAt = now,
            },
            version);
    }

    /// <summary>
    /// Rolls the pointer back by swapping current and previous. A later build that produces the
    /// fingerprint of the set rolled back from creates no set, so the roll back holds (FR-030).
    /// </summary>
    public static DriverSetPointerRecord RollBack(DriverSetPointerRecord current, string previousFingerprint, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.PreviousVersion is not { } previous)
        {
            throw new InvalidOperationException("There is no previous driver set to roll back to.");
        }

        return current with
        {
            RolledBackFromFingerprint = current.CurrentFingerprint,
            CurrentVersion = previous,
            CurrentFingerprint = previousFingerprint,
            PreviousVersion = current.CurrentVersion,
            PendingVersion = null,
            BuiltAt = now,
        };
    }
}
