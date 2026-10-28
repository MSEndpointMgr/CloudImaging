using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>The driver set pinned on a session at assignment, or why none applies.</summary>
/// <param name="ModelId">Matched model.</param>
/// <param name="ModelName">Matched model's name.</param>
/// <param name="Release">The image's Windows release.</param>
/// <param name="SetVersion">Pinned set version; null when no set applies.</param>
/// <param name="SkipReason">Why no set applies.</param>
/// <param name="MatchKey">The device's HP system ID or Dell SKU.</param>
/// <param name="CriticalCategories">JSON array of the vendor's critical categories, when a set is pinned.</param>
public sealed record DriverPin(Guid? ModelId, string? ModelName, WindowsRelease? Release, int? SetVersion, DriverSkipReason? SkipReason, string? MatchKey, string? CriticalCategories)
{
    /// <summary>Writes the pin onto a session.</summary>
    public DeviceSession ApplyTo(DeviceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session with
        {
            DriverModelId = ModelId,
            DriverModelName = ModelName,
            DriverRelease = Release,
            DriverSetVersion = SetVersion,
            DriverSkipReason = SkipReason,
            DriverMatchKey = MatchKey,
            DriverCriticalCategories = CriticalCategories,
        };
    }
}

/// <summary>
/// Matches a device to an onboarded model and pins the model's current set for the image's
/// Windows release (FR-011 to FR-014, operator-and-core-api section 4). Drafts match nothing.
/// The pin is written once; later syncs, edits, critical category changes or removal never change it.
/// </summary>
public sealed class DriverSetResolver
{
    private readonly DriverStore _store;
    private readonly DriverModelRepository _models;
    private readonly DriverSetPointerRepository _pointers;
    private readonly IReadOnlyList<IDriverVendorAdapter> _adapters;

    /// <param name="store">Driver tables.</param>
    /// <param name="models">Onboarded models.</param>
    /// <param name="pointers">Set pointers.</param>
    /// <param name="adapters">Vendor adapters; each says which hardware value its devices match on.</param>
    public DriverSetResolver(DriverStore store, DriverModelRepository models, DriverSetPointerRepository pointers, IEnumerable<IDriverVendorAdapter> adapters)
    {
        _store = store;
        _models = models;
        _pointers = pointers;
        _adapters = adapters.ToList();
    }

    /// <summary>Resolves the pin for a session and the image it is being assigned.</summary>
    public async Task<DriverPin> ResolveAsync(DeviceSession session, OsImage image, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(image);

        // Each vendor's key is tried, the device manufacturer's vendor first.
        var candidates = _adapters
            .OrderByDescending(a => a.IsVendorOf(session.DeviceManufacturer))
            .Select(a => (a.Vendor, Key: Normalize(a.DeviceMatchKey(session.HardwareMetadata))))
            .Where(c => c.Key is not null)
            .Select(c => (c.Vendor, Key: c.Key!))
            .ToList();
        var matchKey = candidates.Count > 0 ? candidates[0].Key : null;

        if (image.WindowsRelease is not { } release)
        {
            return Skip(DriverSkipReason.NoWindowsRelease, matchKey);
        }

        DriverModelRecord? model = null;
        foreach (var (vendor, key) in candidates)
        {
            if (await _models.GetOwnerAsync(vendor, key, ct) is { } ownerId
                && (await _models.GetAsync(ownerId, ct))?.Value is { State: DriverModelState.Active } owner)
            {
                model = owner;
                matchKey = key;
                break;
            }
        }

        if (model is null)
        {
            return Skip(DriverSkipReason.NoMatchingModel, matchKey, release);
        }

        if (model.Architecture != image.Architecture)
        {
            return new DriverPin(model.ModelId, model.DisplayName, release, null, DriverSkipReason.ArchitectureMismatch, matchKey, null);
        }

        if (!model.Releases.Contains(release))
        {
            return new DriverPin(model.ModelId, model.DisplayName, release, null, DriverSkipReason.ReleaseNotOnboarded, matchKey, null);
        }

        if ((await _pointers.GetAsync(model.ModelId, release, ct))?.Value.CurrentVersion is not { } version)
        {
            return new DriverPin(model.ModelId, model.DisplayName, release, null, DriverSkipReason.NoDriverSet, matchKey, null);
        }

        var critical = (await _store.CriticalCategories.ListPartitionAsync(model.Vendor.ToString(), ct)).Select(r => r.Value.Category ?? r.RowKey).ToList();
        return new DriverPin(model.ModelId, model.DisplayName, release, version, null, matchKey, JsonSerializer.Serialize(critical));
    }

    /// <summary>
    /// The pin for a manual choice when imaging starts, or null with the reason when the chosen
    /// model can no longer be used: it was removed, is a draft, has another architecture or has no
    /// current set for the image's release. The device's match key is kept either way.
    /// </summary>
    public async Task<(DriverPin? Pin, string? Reason)> ResolveChoiceAsync(DeviceSession session, OsImage image, DriverChoice choice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var automatic = await ResolveAsync(session, image, ct);
        if (choice.Kind == DriverChoiceKind.None)
        {
            return (new DriverPin(null, null, image.WindowsRelease, null, DriverSkipReason.NoneChosen, automatic.MatchKey, null), null);
        }

        if (image.WindowsRelease is not { } release)
        {
            return (null, "The OS image has no Windows release.");
        }

        var model = choice.ModelId is { } modelId ? (await _models.GetAsync(modelId, ct))?.Value : null;
        if (model is not { State: DriverModelState.Active })
        {
            return (null, "The chosen driver model was removed.");
        }

        if (model.Architecture != image.Architecture)
        {
            return (null, $"{model.DisplayName} is onboarded for {ArchitectureLabel(model.Architecture)}, the OS image is {ArchitectureLabel(image.Architecture)}.");
        }

        if (!model.Releases.Contains(release) || (await _pointers.GetAsync(model.ModelId, release, ct))?.Value.CurrentVersion is not { } version)
        {
            return (null, $"{model.DisplayName} has no driver set for {DriverModelService.ReleaseLabel(release)}.");
        }

        var critical = (await _store.CriticalCategories.ListPartitionAsync(model.Vendor.ToString(), ct)).Select(r => r.Value.Category ?? r.RowKey).ToList();
        return (new DriverPin(model.ModelId, model.DisplayName, release, version, null, automatic.MatchKey, JsonSerializer.Serialize(critical)), null);
    }

    /// <summary>
    /// The models a device can be given for an image: active, same architecture, with a current set
    /// for the image's release; ordered by vendor then name, with the size of that set.
    /// </summary>
    public async Task<IReadOnlyList<DriverAssignmentOption>> OptionsAsync(OsImage image, DriverSetDocumentRepository sets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(sets);
        if (image.WindowsRelease is not { } release)
        {
            return [];
        }

        var options = new List<DriverAssignmentOption>();
        foreach (var model in await _models.ListAsync(ct))
        {
            if (model.State != DriverModelState.Active || model.Architecture != image.Architecture || !model.Releases.Contains(release)
                || (await _pointers.GetAsync(model.ModelId, release, ct))?.Value.CurrentVersion is not { } version)
            {
                continue;
            }

            var document = await sets.GetAsync(model.ModelId, release, version, ct);
            options.Add(new DriverAssignmentOption { ModelId = model.ModelId, Vendor = model.Vendor, DisplayName = model.DisplayName, MaxDownloadBytes = document?.Items.Sum(i => i.Size) ?? 0 });
        }

        return options.OrderBy(o => o.Vendor).ThenBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ArchitectureLabel(MachineArchitecture architecture) => architecture == MachineArchitecture.Arm64 ? "ARM64" : "x64";

    private static DriverPin Skip(DriverSkipReason reason, string? matchKey, WindowsRelease? release = null) =>
        new(null, null, release, null, reason, matchKey, null);

    private static string? Normalize(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : DriverTables.NormalizeKey(key);
}
