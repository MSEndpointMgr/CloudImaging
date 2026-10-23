using CloudImaging.Contracts.Models.SetupProfiles;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Builds the change summary stored on a version row: what was added, removed or changed compared
/// with the version it replaced, matched by the items' stable ids. Pure.
/// </summary>
public static class ChangeSummaryBuilder
{
    /// <summary>Compares <paramref name="next"/> with <paramref name="previous"/>; a null previous means version 1.</summary>
    public static SetupProfileChangeSummary Build(SetupProfileContent? previous, SetupProfileContent next)
    {
        var registryNames = new List<string>();
        var registry = Compare(
            previous?.RegistryValues ?? [], next.RegistryValues, v => v.Id, v => v.Name,
            (a, b) => a.Name == b.Name && a.Hive == b.Hive && a.Key == b.Key && a.ValueName == b.ValueName && a.Type == b.Type && a.Data == b.Data,
            registryNames);

        var scriptNames = new List<string>();
        var scripts = Compare(
            previous?.Scripts ?? [], next.Scripts, s => s.Id, s => s.Name,
            (a, b) => a.Name == b.Name && a.Runs == b.Runs && a.TimeoutMinutes == b.TimeoutMinutes && a.Content == b.Content,
            scriptNames);
        scripts = scripts with { Reordered = previous is not null && IsReordered(previous.Scripts, next.Scripts) };

        var answerFile = (previous?.AnswerFile, next.AnswerFile) switch
        {
            (null, null) => SetupAnswerFileChange.NoChange,
            (null, _) => SetupAnswerFileChange.Added,
            (_, null) => SetupAnswerFileChange.Removed,
            var (before, after) => before!.Content == after!.Content ? SetupAnswerFileChange.NoChange : SetupAnswerFileChange.Changed,
        };

        var failureHandling = previous is not null && previous.FailureHandling != next.FailureHandling
            ? new SetupFailureHandlingChange { From = previous.FailureHandling, To = next.FailureHandling }
            : null;

        var total = registry.Added + registry.Removed + registry.Changed
            + scripts.Added + scripts.Removed + scripts.Changed
            + (scripts.Reordered ? 1 : 0)
            + (answerFile == SetupAnswerFileChange.NoChange ? 0 : 1)
            + (failureHandling is null ? 0 : 1);

        return new SetupProfileChangeSummary
        {
            RegistryValues = registry,
            Scripts = scripts,
            AnswerFile = answerFile,
            FailureHandling = failureHandling,
            Names = new SetupProfileChangeNames
            {
                RegistryValues = [.. registryNames.Take(SetupProfileChangeNames.MaxNamesPerSection)],
                Scripts = [.. scriptNames.Take(SetupProfileChangeNames.MaxNamesPerSection)],
            },
            Total = total,
        };
    }

    private static SetupProfileSectionChange Compare<T>(
        IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, Guid?> id, Func<T, string> name, Func<T, T, bool> same, List<string> names)
    {
        var previous = before.Where(item => id(item) is not null).ToDictionary(item => id(item)!.Value);
        int added = 0, changed = 0;
        foreach (var item in after)
        {
            if (id(item) is not { } key || !previous.TryGetValue(key, out var old))
            {
                added++;
                names.Add(name(item));
            }
            else if (!same(old, item))
            {
                changed++;
                names.Add(name(item));
            }
        }

        var current = after.Select(id).OfType<Guid>().ToHashSet();
        var removedItems = before.Where(item => id(item) is not { } key || !current.Contains(key)).ToList();
        names.AddRange(removedItems.Select(name));
        return new SetupProfileSectionChange { Added = added, Removed = removedItems.Count, Changed = changed };
    }

    private static bool IsReordered(IReadOnlyList<SetupScript> before, IReadOnlyList<SetupScript> after)
    {
        var kept = after.Select(s => s.Id).OfType<Guid>().ToHashSet();
        kept.IntersectWith(before.Select(s => s.Id).OfType<Guid>());
        static IEnumerable<Guid> Sequence(IReadOnlyList<SetupScript> scripts, HashSet<Guid> kept) =>
            scripts.OrderBy(s => s.Runs).ThenBy(s => s.Order).Select(s => s.Id).OfType<Guid>().Where(kept.Contains);
        return !Sequence(before, kept).SequenceEqual(Sequence(after, kept));
    }
}
