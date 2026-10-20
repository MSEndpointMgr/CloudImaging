using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>Create/update input for a Wi-Fi network. The password is write-only.</summary>
public sealed class WifiNetworkInput
{
    /// <summary>Network name.</summary>
    public string? Ssid { get; init; }

    /// <summary>True when the network does not broadcast its name.</summary>
    public bool Hidden { get; init; }

    /// <summary>Security type; required.</summary>
    public WifiSecurityType? Security { get; init; }

    /// <summary>Password; required on create, omitted on update to keep the stored one.</summary>
    public string? Password { get; init; }

    /// <summary>True to send the network to every location.</summary>
    public bool AllLocations { get; init; }

    /// <summary>Locations that receive the network when <see cref="AllLocations"/> is false.</summary>
    public IReadOnlyList<Guid>? LocationIds { get; init; }

    /// <summary>Null keeps the current value (true on create).</summary>
    public bool? Enabled { get; init; }

    /// <summary>Signed-in administrator, recorded when the password changes.</summary>
    public string? ChangedBy { get; init; }
}

/// <summary>Outcome of a catalog change: the saved network, a validation message, or not found.</summary>
/// <param name="Network">The saved network on success.</param>
/// <param name="Error">Validation message to show the administrator.</param>
/// <param name="NotFound">True when the network or location does not exist.</param>
public sealed record WifiNetworkResult(WifiNetwork? Network, string? Error = null, bool NotFound = false)
{
    internal static WifiNetworkResult Invalid(string error) => new(null, error);

    internal static readonly WifiNetworkResult Missing = new(null, null, true);
}

/// <summary>
/// Rules for saved Wi-Fi networks: validation, order, per-location selection, and building the
/// password-bearing set a device or Media Builder receives.
/// </summary>
public sealed partial class WifiNetworkCatalog
{
    private readonly WifiNetworkRepository _networks;
    private readonly LocationRepository _locations;
    private readonly WifiPasswordStore _passwords;
    private readonly TimeProvider _time;
    private readonly ILogger<WifiNetworkCatalog> _logger;

    /// <param name="networks">Wi-Fi network repository.</param>
    /// <param name="locations">Location repository, to validate location ids.</param>
    /// <param name="passwords">Key Vault password store.</param>
    /// <param name="logger">Logger for catalog changes.</param>
    /// <param name="time">Clock; defaults to the system clock.</param>
    public WifiNetworkCatalog(WifiNetworkRepository networks, LocationRepository locations, WifiPasswordStore passwords, ILogger<WifiNetworkCatalog> logger, TimeProvider? time = null)
    {
        _networks = networks;
        _locations = locations;
        _passwords = passwords;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Lists networks in order, without passwords.</summary>
    public Task<List<WifiNetwork>> ListAsync(CancellationToken ct = default) => _networks.ListAsync(ct);

    /// <summary>Creates a network at the end of the order and stores its password.</summary>
    public async Task<WifiNetworkResult> CreateAsync(WifiNetworkInput input, CancellationToken ct = default)
    {
        var existing = await _networks.ListAsync(ct);
        if (existing.Count >= WifiNetwork.MaxNetworks)
        {
            return WifiNetworkResult.Invalid($"At most {WifiNetwork.MaxNetworks} Wi-Fi networks can be saved.");
        }

        var error = await ValidateAsync(input, null, existing, ct) ?? WifiNetwork.ValidatePassword(input.Password);
        if (error is not null)
        {
            return WifiNetworkResult.Invalid(error);
        }

        var now = _time.GetUtcNow();
        var network = new WifiNetwork
        {
            NetworkId = Guid.NewGuid(),
            Ssid = input.Ssid!,
            Hidden = input.Hidden,
            Security = input.Security!.Value,
            AllLocations = input.AllLocations,
            LocationIds = input.AllLocations ? [] : Distinct(input.LocationIds),
            Order = existing.Count == 0 ? 1 : existing.Max(n => n.Order) + 1,
            Enabled = input.Enabled ?? true,
            PasswordChangedAt = now,
            PasswordChangedBy = input.ChangedBy,
            CreatedAt = now,
        };

        // Password first: a stray secret harms nothing, a row without a password would silently send nothing.
        await _passwords.SetAsync(network.NetworkId, input.Password!, ct);
        await _networks.AddAsync(network, ct);
        LogCreated(_logger, network.NetworkId, network.Ssid);
        return new WifiNetworkResult(network);
    }

    /// <summary>Updates a network. An empty password keeps the stored one.</summary>
    public async Task<WifiNetworkResult> UpdateAsync(Guid networkId, WifiNetworkInput input, CancellationToken ct = default)
    {
        var existing = await _networks.ListAsync(ct);
        var current = existing.Find(n => n.NetworkId == networkId);
        if (current is null)
        {
            return WifiNetworkResult.Missing;
        }

        var replacePassword = !string.IsNullOrEmpty(input.Password);
        var error = await ValidateAsync(input, networkId, existing, ct) ?? (replacePassword ? WifiNetwork.ValidatePassword(input.Password) : null);
        if (error is not null)
        {
            return WifiNetworkResult.Invalid(error);
        }

        var updated = new WifiNetwork
        {
            NetworkId = networkId,
            Ssid = input.Ssid!,
            Hidden = input.Hidden,
            Security = input.Security!.Value,
            AllLocations = input.AllLocations,
            LocationIds = input.AllLocations ? [] : Distinct(input.LocationIds),
            Order = current.Order,
            Enabled = input.Enabled ?? current.Enabled,
            PasswordChangedAt = replacePassword ? _time.GetUtcNow() : current.PasswordChangedAt,
            PasswordChangedBy = replacePassword ? input.ChangedBy : current.PasswordChangedBy,
            CreatedAt = current.CreatedAt,
        };

        if (replacePassword)
        {
            await _passwords.SetAsync(networkId, input.Password!, ct);
        }
        if (!await _networks.UpdateAsync(updated, ct))
        {
            return WifiNetworkResult.Missing;
        }
        LogUpdated(_logger, networkId, replacePassword);
        return new WifiNetworkResult(updated);
    }

    /// <summary>Deletes a network and its password, then closes the gap in the order.</summary>
    public async Task DeleteAsync(Guid networkId, CancellationToken ct = default)
    {
        await _networks.DeleteAsync(networkId, ct);
        await _passwords.DeleteAsync(networkId, ct);
        await RenumberAsync(await _networks.ListAsync(ct), ct);
        LogDeleted(_logger, networkId);
    }

    /// <summary>Sets the order devices try networks in. Returns a validation message, or null on success.</summary>
    public async Task<string?> SetOrderAsync(IReadOnlyList<Guid>? networkIds, CancellationToken ct = default)
    {
        var existing = await _networks.ListAsync(ct);
        var ids = networkIds ?? [];
        if (ids.Count != existing.Count || ids.Distinct().Count() != ids.Count || !existing.TrueForAll(n => ids.Contains(n.NetworkId)))
        {
            return "The order must list every saved network once. Reload the page and try again.";
        }

        var byId = existing.ToDictionary(n => n.NetworkId);
        await RenumberAsync([.. ids.Select(i => byId[i])], ct);
        return null;
    }

    /// <summary>
    /// Sets the networks a location receives by changing each selected-locations network's list.
    /// "All locations" networks are left as they are.
    /// </summary>
    public async Task<WifiNetworkResult> SetLocationNetworksAsync(Guid locationId, IReadOnlyList<Guid>? networkIds, CancellationToken ct = default)
    {
        if (await _locations.GetByIdAsync(locationId, ct) is null)
        {
            return WifiNetworkResult.Missing;
        }

        var wanted = networkIds?.ToHashSet() ?? [];
        var existing = await _networks.ListAsync(ct);
        if (wanted.Any(w => existing.TrueForAll(n => n.NetworkId != w)))
        {
            return WifiNetworkResult.Invalid("One of the networks no longer exists. Reload the page and try again.");
        }

        foreach (var network in existing.Where(n => !n.AllLocations))
        {
            var has = network.LocationIds.Contains(locationId);
            var want = wanted.Contains(network.NetworkId);
            if (has == want)
            {
                continue;
            }
            List<Guid> locationIds = want ? [.. network.LocationIds, locationId] : [.. network.LocationIds.Where(l => l != locationId)];
            await _networks.UpdateAsync(WifiNetworkRepository.With(network, locationIds: locationIds), ct);
        }
        LogLocationNetworksSet(_logger, locationId, wanted.Count);
        return new WifiNetworkResult(null);
    }

    /// <summary>The enabled networks that apply to a location, with passwords, in order.</summary>
    /// <param name="locationId">Location from the USB media; null receives "all locations" networks only.</param>
    /// <param name="purpose">Who the passwords are read for, recorded in the audit log.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ManagedWifiNetworkSet> BuildManagedSetAsync(Guid? locationId, string purpose, CancellationToken ct = default)
    {
        var networks = new List<ManagedWifiNetwork>();
        foreach (var network in (await _networks.ListAsync(ct)).Where(n => n.AppliesTo(locationId)))
        {
            var password = await _passwords.GetAsync(network.NetworkId, purpose, ct);
            if (password is not null)
            {
                networks.Add(new ManagedWifiNetwork(network.Ssid, network.Hidden, network.Security, password));
            }
        }
        return new ManagedWifiNetworkSet { LocationId = locationId, GeneratedAt = _time.GetUtcNow(), Networks = networks };
    }

    private async Task<string?> ValidateAsync(WifiNetworkInput input, Guid? id, List<WifiNetwork> existing, CancellationToken ct)
    {
        var ssidError = WifiNetwork.ValidateSsid(input.Ssid);
        if (ssidError is not null)
        {
            return ssidError;
        }
        if (existing.Exists(n => n.NetworkId != id && string.Equals(n.Ssid, input.Ssid, StringComparison.Ordinal)))
        {
            return $"A network named \"{input.Ssid}\" already exists.";
        }
        if (input.Security is null || !Enum.IsDefined(input.Security.Value))
        {
            return "Choose a security type.";
        }
        if (input.AllLocations || input.LocationIds is null || input.LocationIds.Count == 0)
        {
            return null;
        }

        var known = new HashSet<Guid>();
        await foreach (var location in _locations.ListAllAsync(ct))
        {
            known.Add(location.LocationId);
        }
        return input.LocationIds.All(known.Contains) ? null : "One of the selected locations no longer exists. Reload the page and try again.";
    }

    private async Task RenumberAsync(List<WifiNetwork> ordered, CancellationToken ct)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].Order != i + 1)
            {
                await _networks.UpdateAsync(WifiNetworkRepository.With(ordered[i], order: i + 1), ct);
            }
        }
    }

    private static List<Guid> Distinct(IReadOnlyList<Guid>? ids) => ids is null ? [] : [.. ids.Distinct()];

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi network {NetworkId} ({Ssid}) created.")]
    private static partial void LogCreated(ILogger logger, Guid networkId, string ssid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi network {NetworkId} updated (password replaced: {PasswordReplaced}).")]
    private static partial void LogUpdated(ILogger logger, Guid networkId, bool passwordReplaced);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wi-Fi network {NetworkId} deleted.")]
    private static partial void LogDeleted(ILogger logger, Guid networkId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Location {LocationId} now receives {Count} selected Wi-Fi networks.")]
    private static partial void LogLocationNetworksSet(ILogger logger, Guid locationId, int count);
}
