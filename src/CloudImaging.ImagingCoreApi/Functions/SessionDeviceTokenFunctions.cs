using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// The device-session token, called only by the Device Gateway.
///
/// PUT  internal/sessions/{id}/device-token          record the token hash and expiry at session creation
/// POST internal/sessions/{id}/device-token/verify   204 when the hash matches and has not expired, 401 otherwise
///
/// Only the SHA-256 of the token is stored; the token itself never reaches Imaging Core.
/// </summary>
public sealed partial class SessionDeviceTokenFunctions
{
    private readonly DeviceSessionRepository _sessions;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionDeviceTokenFunctions> _logger;

    /// <param name="sessions">Device sessions.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public SessionDeviceTokenFunctions(DeviceSessionRepository sessions, TimeProvider time, ILogger<SessionDeviceTokenFunctions> logger)
    {
        _sessions = sessions;
        _time = time;
        _logger = logger;
    }

    /// <summary>PUT internal/sessions/{id}/device-token.</summary>
    [Function("SetSessionDeviceToken")]
    public async Task<HttpResponseData> SetToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/sessions/{id}/device-token")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var (hash, expiresAt) = await ReadAsync(req, ct);
        if (!Guid.TryParse(id, out var sessionId) || hash is null || expiresAt is not { } expiry || expiry <= _time.GetUtcNow())
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        if (!await _sessions.SetDeviceTokenAsync(sessionId, hash, expiry, ct))
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    /// <summary>POST internal/sessions/{id}/device-token/verify.</summary>
    [Function("VerifySessionDeviceToken")]
    public async Task<HttpResponseData> VerifyToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{id}/device-token/verify")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var (hash, _) = await ReadAsync(req, ct);
        if (!Guid.TryParse(id, out var sessionId) || hash is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var session = await _sessions.GetByIdAsync(sessionId, ct);
        if (session?.DeviceSessionTokenHash is not { } stored
            || session.DeviceSessionTokenExpiresAt is not { } expiresAt
            || expiresAt <= _time.GetUtcNow()
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(hash)))
        {
            LogTokenRejected(_logger, sessionId);
            return req.CreateResponse(HttpStatusCode.Unauthorized);
        }

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    // Accepts only a lower-case hex SHA-256, so nothing else is ever stored or compared.
    private static async Task<(string? Hash, DateTimeOffset? ExpiresAt)> ReadAsync(HttpRequestData req, CancellationToken ct)
    {
        try
        {
            using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct);
            var root = body.RootElement;
            var hash = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tokenHash", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            DateTimeOffset? expiresAt = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("expiresAt", out var e) && e.TryGetDateTimeOffset(out var parsed) ? parsed : null;
            return (hash is { Length: 64 } && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? hash : null, expiresAt);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device-session token rejected for session {SessionId}.")]
    private static partial void LogTokenRejected(ILogger logger, Guid sessionId);
}
