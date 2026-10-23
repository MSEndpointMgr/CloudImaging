using CloudImaging.ImagingCoreApi.Repositories;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>The setup profile version a session gets.</summary>
/// <param name="ProfileId">Profile id.</param>
/// <param name="Version">Current version at resolution time.</param>
/// <param name="Name">Profile name at resolution time.</param>
public sealed record ResolvedSetupProfile(Guid ProfileId, int Version, string Name);

/// <summary>
/// Resolves the setup profile for a device from the location on its USB media: the profile that
/// targets that location, otherwise the All devices profile, otherwise none.
/// </summary>
public sealed class SetupProfileResolver
{
    private readonly SetupProfileTargetRepository _targets;
    private readonly SetupProfileRepository _profiles;

    /// <param name="targets">Targeting index.</param>
    /// <param name="profiles">Profile rows.</param>
    public SetupProfileResolver(SetupProfileTargetRepository targets, SetupProfileRepository profiles)
    {
        _targets = targets;
        _profiles = profiles;
    }

    /// <summary>Returns the profile and current version for a location, or null when none applies.</summary>
    public async Task<ResolvedSetupProfile?> ResolveAsync(Guid? locationId, CancellationToken ct = default)
    {
        if (locationId is { } id && await ResolveTargetAsync(SetupProfileTargetRepository.LocationKey(id), ct) is { } located)
        {
            return located;
        }
        return await ResolveTargetAsync(SetupProfileTargetRepository.AllDevicesKey, ct);
    }

    private async Task<ResolvedSetupProfile?> ResolveTargetAsync(string targetKey, CancellationToken ct)
    {
        if (await _targets.GetOwnerAsync(targetKey, ct) is not { } owner)
        {
            return null;
        }
        var profile = await _profiles.GetAsync(owner, ct);
        return profile is null || profile.IsDeleted || profile.CurrentVersion < 1
            ? null
            : new ResolvedSetupProfile(profile.ProfileId, profile.CurrentVersion, profile.Name);
    }
}
