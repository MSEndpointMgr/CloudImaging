using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Wi-Fi passwords in Key Vault, one secret per network (<c>wifi-network-{networkId:N}</c>).
/// Every read is logged with its purpose so access can be audited; the value itself never is.
/// </summary>
public partial class WifiPasswordStore
{
    private const string SecretNamePrefix = "wifi-network-";

    private readonly SecretClient? _kvClient;
    private readonly ILogger<WifiPasswordStore> _logger;

    /// <param name="kvClient">Key Vault secrets client.</param>
    /// <param name="logger">Logger for this store.</param>
    public WifiPasswordStore(SecretClient kvClient, ILogger<WifiPasswordStore> logger)
    {
        _kvClient = kvClient;
        _logger = logger;
    }

    /// <summary>For test doubles that override every member.</summary>
    protected WifiPasswordStore(ILogger<WifiPasswordStore> logger) => _logger = logger;

    /// <summary>Stores or replaces the password for a network.</summary>
    public virtual async Task SetAsync(Guid networkId, string password, CancellationToken ct = default)
    {
        var secret = new KeyVaultSecret(SecretName(networkId), password);
        secret.Properties.ContentType = "text/plain";
        secret.Properties.Tags["Purpose"] = "wifi-network";
        await Client.SetSecretAsync(secret, ct);
        LogStored(_logger, networkId);
    }

    /// <summary>Returns the password for a network, or null when none is stored.</summary>
    /// <param name="networkId">Network whose password is read.</param>
    /// <param name="purpose">Who the password is read for (e.g. "media-builder", "device-gateway"), for the audit log.</param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<string?> GetAsync(Guid networkId, string purpose, CancellationToken ct = default)
    {
        try
        {
            var response = await Client.GetSecretAsync(SecretName(networkId), cancellationToken: ct);
            LogRead(_logger, networkId, purpose);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            LogMissing(_logger, networkId);
            return null;
        }
    }

    /// <summary>Deletes the password for a network. No-op when none is stored.</summary>
    public virtual async Task DeleteAsync(Guid networkId, CancellationToken ct = default)
    {
        try
        {
            await Client.StartDeleteSecretAsync(SecretName(networkId), ct);
            LogDeleted(_logger, networkId);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    private SecretClient Client => _kvClient ?? throw new InvalidOperationException("Key Vault is not configured.");

    private static string SecretName(Guid networkId) => $"{SecretNamePrefix}{networkId:N}";

    [LoggerMessage(Level = LogLevel.Information, Message = "Stored the password for Wi-Fi network {NetworkId} in Key Vault.")]
    private static partial void LogStored(ILogger logger, Guid networkId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Read the password for Wi-Fi network {NetworkId} from Key Vault for {Purpose}.")]
    private static partial void LogRead(ILogger logger, Guid networkId, string purpose);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No password is stored for Wi-Fi network {NetworkId}; it is left out.")]
    private static partial void LogMissing(ILogger logger, Guid networkId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted the password for Wi-Fi network {NetworkId} from Key Vault.")]
    private static partial void LogDeleted(ILogger logger, Guid networkId);
}
