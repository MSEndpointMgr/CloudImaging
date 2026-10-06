using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Administrator overrides for devices blocked by pre-flight checks. Portal-facing, called by the
/// Operator API over Private Link; the portal backend enforces the Administrator role and supplies
/// the acting user from their validated token.
///
/// GET    /api/internal/preflight-overrides                             active overrides + whether pre-flight is on
/// POST   /api/internal/sessions/{id}/preflight-override                approve a blocked session's device
/// DELETE /api/internal/preflight-overrides?serialNumber=&amp;revokedBy=   revoke a device's override
/// </summary>
public sealed partial class PreFlightOverrideFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PreFlightOverrideRepository _overrideRepo;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly ILogger<PreFlightOverrideFunctions> _logger;

    public PreFlightOverrideFunctions(
        PreFlightOverrideRepository overrideRepo,
        DeviceSessionRepository sessionRepo,
        PortalConfigurationRepository configRepo,
        ILogger<PreFlightOverrideFunctions> logger)
    {
        _overrideRepo = overrideRepo;
        _sessionRepo = sessionRepo;
        _configRepo = configRepo;
        _logger = logger;
    }

    [Function("ListPreFlightOverrides")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/preflight-overrides")] HttpRequestData req,
        FunctionContext context)
    {
        var config = await _configRepo.GetAsync(context.CancellationToken);
        var now = DateTimeOffset.UtcNow;
        var overrides = new List<PreFlightOverride>();
        await foreach (var o in _overrideRepo.ListAsync(context.CancellationToken))
        {
            if (o.ExpiresAt > now)
            {
                overrides.Add(o);
            }
        }

        var body = new
        {
            preFlightEnabled = config.DevicePreFlightAuthorizationEnabled,
            overrides = overrides.OrderByDescending(o => o.ApprovedAt),
        };
        return await JsonAsync(req, HttpStatusCode.OK, body, context.CancellationToken);
    }

    [Function("ApprovePreFlightOverride")]
    public async Task<HttpResponseData> Approve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{id}/preflight-override")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(id, out var sessionId))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid session id.", ct);
        }

        var approval = await ReadAsync<OverrideApproval>(req, ct);
        if (approval is null || string.IsNullOrWhiteSpace(approval.ApprovedBy))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "The approving user is required.", ct);
        }

        var config = await _configRepo.GetAsync(ct);
        if (!config.DevicePreFlightAuthorizationEnabled)
        {
            return await ProblemAsync(req, HttpStatusCode.Conflict, "Pre-flight authorization is off, so there is nothing to override.", ct);
        }

        var session = await _sessionRepo.GetByIdAsync(sessionId, ct);
        if (session is null)
        {
            return await ProblemAsync(req, HttpStatusCode.NotFound, "Session not found.", ct);
        }

        if (session.State != SessionState.SessionNotAuthorized)
        {
            return await ProblemAsync(req, HttpStatusCode.Conflict, "Only a blocked session can be approved.", ct);
        }

        var covered = CoveredChecks(session);
        if (covered.Count == 0)
        {
            return await ProblemAsync(req, HttpStatusCode.Conflict, "This session has no failed pre-flight checks to approve.", ct);
        }

        var now = DateTimeOffset.UtcNow;
        var preFlightOverride = new PreFlightOverride
        {
            SerialNumber = session.DeviceSerialNumber,
            CoveredChecks = covered,
            SourceSessionId = session.SessionId,
            DeviceManufacturer = session.DeviceManufacturer,
            DeviceModel = session.DeviceModel,
            LocationName = session.LocationName,
            SourceChecks = session.PreFlightChecks,
            ApprovedBy = approval.ApprovedBy.Trim(),
            ApprovedByObjectId = approval.ApprovedByObjectId,
            ApprovedAt = now,
            ExpiresAt = now + PreFlightCheckEvaluator.OverrideLifetime,
        };

        await _overrideRepo.UpsertAsync(preFlightOverride, ct);
        LogApproved(_logger, session.DeviceSerialNumber, session.SessionId, preFlightOverride.ApprovedBy, string.Join(", ", covered));
        return await JsonAsync(req, HttpStatusCode.OK, preFlightOverride, ct);
    }

    [Function("RevokePreFlightOverride")]
    public async Task<HttpResponseData> Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/preflight-overrides")] HttpRequestData req,
        FunctionContext context)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var serialNumber = query.Get("serialNumber");
        var revokedBy = query.Get("revokedBy");
        if (string.IsNullOrWhiteSpace(serialNumber) || string.IsNullOrWhiteSpace(revokedBy))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "serialNumber and revokedBy are required.", context.CancellationToken);
        }

        if (!await _overrideRepo.DeleteAsync(serialNumber, context.CancellationToken))
        {
            return await ProblemAsync(req, HttpStatusCode.NotFound, "No override exists for this device.", context.CancellationToken);
        }

        LogRevoked(_logger, serialNumber, revokedBy);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    /// <summary>
    /// The checks an override for <paramref name="session"/> would cover. Sessions blocked before the
    /// per-check results existed only ever failed the enrollment check.
    /// </summary>
    internal static IReadOnlyList<PreFlightCheck> CoveredChecks(DeviceSession session) =>
        session.PreFlightChecks.Count > 0
            ? PreFlightCheckEvaluator.FailedChecks(session.PreFlightChecks)
            : session.PreFlightAuthorizationResult == PreFlightAuthorizationResult.NotAuthorized
                ? [PreFlightCheck.AutopilotPresence]
                : [];

    private static async Task<T?> ReadAsync<T>(HttpRequestData req, CancellationToken ct)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<HttpResponseData> JsonAsync<T>(HttpRequestData req, HttpStatusCode status, T body, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions), ct);
        return response;
    }

    private static async Task<HttpResponseData> ProblemAsync(HttpRequestData req, HttpStatusCode status, string detail, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { title = status.ToString(), status = (int)status, detail }, JsonOptions), ct);
        return response;
    }

    private sealed class OverrideApproval
    {
        public string? ApprovedBy { get; init; }
        public string? ApprovedByObjectId { get; init; }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Pre-flight override for device {SerialNumber} approved from session {SessionId} by {ApprovedBy}, covering: {CoveredChecks}.")]
    private static partial void LogApproved(ILogger logger, string serialNumber, Guid sessionId, string approvedBy, string coveredChecks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pre-flight override for device {SerialNumber} revoked by {RevokedBy}.")]
    private static partial void LogRevoked(ILogger logger, string serialNumber, string revokedBy);
}
