using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal session query endpoints consumed by the Operator API for the portal devices view
/// (FR-031). These return a secret-free projection of a <see cref="DeviceSession"/> — the
/// passcode hash, device-session token, and SAS token URL are NEVER exposed to the portal.
///
/// GET /api/internal/sessions        — list all sessions (active + terminal), optional state filter
/// GET /api/internal/sessions/{id}   — get a single session summary
/// GET /api/internal/sessions/{sessionId}/status — device-facing status poll (secrets included:
///   this is called only by Device Gateway API over Private Link, never by the portal)
/// </summary>
public sealed partial class SessionQueryFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DeviceSessionRepository _sessionRepo;
    private readonly OsImageRepository _osImageRepo;
    private readonly ILogger<SessionQueryFunctions> _logger;

    public SessionQueryFunctions(
        DeviceSessionRepository sessionRepo,
        OsImageRepository osImageRepo,
        ILogger<SessionQueryFunctions> logger)
    {
        _sessionRepo = sessionRepo;
        _osImageRepo = osImageRepo;
        _logger = logger;
    }

    // ── GET /api/internal/sessions ───────────────────────────────────────────

    [Function("GetSessions")]
    public async Task<HttpResponseData> GetSessions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions")] HttpRequestData req,
        FunctionContext context)
    {
        var filter = System.Web.HttpUtility.ParseQueryString(req.Url.Query).Get("filter");

        var summaries = new List<SessionSummary>();
        await foreach (var session in _sessionRepo.QueryAllAsync(context.CancellationToken))
        {
            if (filter is not null &&
                !session.State.ToString().Equals(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            summaries.Add(ToSummary(session));
        }

        LogSessionsListed(_logger, summaries.Count);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(summaries, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── GET /api/internal/sessions/{id} ──────────────────────────────────────

    [Function("GetSessionById")]
    public async Task<HttpResponseData> GetSessionById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions/{id}")] HttpRequestData req,
        string id, FunctionContext context)
    {
        if (!Guid.TryParse(id, out var sessionId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var session = await _sessionRepo.GetByIdAsync(sessionId, context.CancellationToken);
        if (session is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(ToSummary(session), JsonOptions), context.CancellationToken);
        return response;
    }

    // ── GET /api/internal/sessions/{sessionId}/status ────────────────────────

    [Function("GetSessionStatus")]
    public async Task<HttpResponseData> GetSessionStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions/{sessionId}/status")] HttpRequestData req,
        string sessionId, FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var session = await _sessionRepo.GetByIdAsync(sessionGuid, context.CancellationToken);
        if (session is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        // A successful device status poll IS the heartbeat (spec FR-021). Recording it here is what
        // keeps a session alive through steps that report no progress of their own, and through the
        // assignment wait. Best-effort: a failed stamp must not fail the poll the client depends on.
        try
        {
            await _sessionRepo.TouchHeartbeatAsync(sessionGuid, DateTimeOffset.UtcNow, context.CancellationToken);
        }
        catch (Exception ex)
        {
            LogHeartbeatStampFailed(_logger, ex, sessionGuid);
        }

        string? sha256Hash = null;
        if (session.AssignedOsImageId is Guid assignedId)
        {
            var image = await _osImageRepo.GetByIdAsync(assignedId, context.CancellationToken);
            sha256Hash = image?.Sha256Hash;
        }

        object? partitioningScheme = session.PartitioningSchemeSnapshotJson is string schemeJson
            ? JsonSerializer.Deserialize<JsonElement>(schemeJson, JsonOptions)
            : null;

        var responseBody = new
        {
            sessionId = session.SessionId,
            state = session.State.ToString(),
            currentStep = session.CurrentStep,
            overallProgressPercent = session.OverallProgressPercent,
            sasTokenUrl = session.SasTokenUrl,
            sasTokenUrlExpiresAt = session.SasTokenUrlExpiresAt,
            sha256Hash,
            partitioningScheme,
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(responseBody, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── Projection ────────────────────────────────────────────────────────────

    private static SessionSummary ToSummary(DeviceSession s) => new(
        s.SessionId,
        s.State.ToString(),
        s.DeviceSerialNumber,
        s.DeviceManufacturer,
        s.DeviceModel,
        s.MacAddress,
        ToHardwareSummary(s.HardwareMetadata),
        s.LocationId,
        s.LocationName,
        s.PreFlightAuthorizationResult.ToString(),
        s.AssignedOsImageId,
        s.OverallProgressPercent,
        s.CurrentStep,
        s.CreatedAt,
        s.LastHeartbeatAt,
        s.TerminalAt);

    /// <summary>
    /// Deliberately re-declared rather than returning <see cref="DeviceHardwareMetadata"/> directly:
    /// this is a service boundary, so the portal projection stays an explicit allow-list. Adding a
    /// field to the domain model must not silently publish it to the portal.
    /// </summary>
    private static HardwareSummary? ToHardwareSummary(DeviceHardwareMetadata? h) =>
        h is null ? null : new HardwareSummary(
            h.MotherboardManufacturer,
            h.MotherboardModel,
            h.BiosVersion,
            h.NicIdentifiers,
            h.StorageLayout);

    /// <summary>Secret-free portal projection of a device session.</summary>
    private sealed record SessionSummary(
        Guid SessionId,
        string State,
        string DeviceSerialNumber,
        string DeviceManufacturer,
        string DeviceModel,
        string? MacAddress,
        HardwareSummary? Hardware,
        Guid? LocationId,
        string? LocationName,
        string PreFlightAuthorizationResult,
        Guid? AssignedOsImageId,
        int OverallProgressPercent,
        string? CurrentStep,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastHeartbeatAt,
        DateTimeOffset? TerminalAt);

    /// <summary>
    /// Hardware inventory collected silently at session init (FR-001a). Informational only — every
    /// field is nullable/empty-able because WMI is not guaranteed to answer in WinPE.
    /// </summary>
    private sealed record HardwareSummary(
        string? MotherboardManufacturer,
        string? MotherboardModel,
        string? BiosVersion,
        IReadOnlyList<string> NicIdentifiers,
        IReadOnlyList<string> StorageLayout);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listed {Count} device session summaries for portal.")]
    private static partial void LogSessionsListed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to stamp heartbeat for session {SessionId}.")]
    private static partial void LogHeartbeatStampFailed(ILogger logger, Exception ex, Guid sessionId);
}
