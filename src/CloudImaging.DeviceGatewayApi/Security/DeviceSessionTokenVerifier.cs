using System.Collections.Concurrent;
using System.Net;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Security;

/// <summary>Outcome of a device-session token check.</summary>
public enum DeviceTokenCheck
{
    /// <summary>The token is the session's current, unexpired token.</summary>
    Valid,

    /// <summary>Malformed, unknown, replaced or expired.</summary>
    Invalid,

    /// <summary>Imaging Core could not be asked; the request is refused, never let through.</summary>
    Unavailable,
}

/// <summary>
/// Checks a device-session bearer token against the hash Imaging Core stored when the session
/// was created. A confirmed token is remembered for one minute so a polling device does not
/// cost an extra Imaging Core call per request; refusals are never cached.
/// </summary>
public sealed partial class DeviceSessionTokenVerifier
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private readonly Func<Guid, string, CancellationToken, Task<HttpStatusCode>> _verify;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _confirmed = new(StringComparer.Ordinal);

    /// <param name="verify">Asks Imaging Core; returns its status code.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">Clock.</param>
    public DeviceSessionTokenVerifier(Func<Guid, string, CancellationToken, Task<HttpStatusCode>> verify, ILogger logger, TimeProvider? time = null)
    {
        _verify = verify;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Builds a verifier over the Imaging Core client.</summary>
    public static DeviceSessionTokenVerifier ForCore(Func<ImagingCoreClient> client, ILogger logger) =>
        new(async (sessionId, hash, ct) =>
        {
            using var response = await client().VerifyDeviceTokenAsync(sessionId, hash, ct);
            return response.StatusCode;
        }, logger);

    /// <summary>Checks a token and returns the session it authenticates when valid.</summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<(DeviceTokenCheck Check, Guid SessionId)> CheckAsync(string token, CancellationToken ct)
    {
        if (DeviceSessionTokenService.ExtractSessionId(token) is not { } sessionId)
        {
            return (DeviceTokenCheck.Invalid, Guid.Empty);
        }

        var hash = DeviceSessionTokenService.Hash(token);
        var now = _time.GetUtcNow();
        if (_confirmed.TryGetValue(hash, out var until) && until > now)
        {
            return (DeviceTokenCheck.Valid, sessionId);
        }

        HttpStatusCode status;
        try
        {
            status = await _verify(sessionId, hash, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogUnavailable(_logger, ex.Message);
            return (DeviceTokenCheck.Unavailable, sessionId);
        }

        if (status == HttpStatusCode.NoContent)
        {
            if (_confirmed.Count > 10_000)
            {
                _confirmed.Clear();
            }

            _confirmed[hash] = now + CacheLifetime;
            return (DeviceTokenCheck.Valid, sessionId);
        }

        _confirmed.TryRemove(hash, out _);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            return (DeviceTokenCheck.Invalid, sessionId);
        }

        LogUnavailable(_logger, $"Imaging Core answered {(int)status}.");
        return (DeviceTokenCheck.Unavailable, sessionId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device-session token could not be verified: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);
}
