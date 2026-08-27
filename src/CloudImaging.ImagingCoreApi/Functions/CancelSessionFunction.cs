using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// DELETE /api/internal/sessions/{sessionId} — Immediately removes a coupled session that was
/// aborted before imaging started (e.g. the device or Hyper-V VM was rebooted/powered off after
/// coupling), instead of leaving it stuck in the portal's "Coupled Devices" list until the
/// 30-minute inactivity timeout plus 24-hour terminal purge run (see
/// <see cref="CloudImaging.ImagingCoreApi.Services.DeviceSessionLifecycleService"/>).
///
/// Scoped to <see cref="SessionState.SessionAssigned"/> only — once imaging has actually
/// started (or completed/failed), the session record has real diagnostic value and must go
/// through the normal lifecycle (expiry → terminal → scheduled purge) rather than being deleted
/// on demand.
///
/// Response codes:
///   204 No Content — session removed.
///   404 Not Found — session not found.
///   409 Conflict — session is not in a removable (SessionAssigned) state.
/// </summary>
public sealed partial class CancelSessionFunction
{
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly ILogger<CancelSessionFunction> _logger;

    public CancelSessionFunction(
        DeviceSessionRepository sessionRepo,
        ILogger<CancelSessionFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _logger = logger;
    }

    [Function(nameof(CancelSessionFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/sessions/{sessionId}")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
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

        if (session.State != SessionState.SessionAssigned)
        {
            LogInvalidStateForCancel(_logger, sessionGuid, session.State);
            return req.CreateResponse(HttpStatusCode.Conflict);
        }

        await _sessionRepo.DeleteAsync(sessionGuid, context.CancellationToken);
        LogSessionCancelled(_logger, sessionGuid);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Coupled session {SessionId} removed by operator request.")]
    private static partial void LogSessionCancelled(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot remove session {SessionId}: not in SessionAssigned state (current: {State}).")]
    private static partial void LogInvalidStateForCancel(ILogger logger, Guid sessionId, SessionState state);
}
