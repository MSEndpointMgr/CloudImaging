using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// The Firmware tab of a model, called by the Operator API on behalf of the portal, and the BIOS
/// pinned on a session, called only by the Device Gateway.
///
/// GET    internal/drivers/models/{id}/firmware
/// PUT    internal/drivers/models/{id}/firmware/{platform}/action     { action, actor }
/// POST   internal/drivers/models/{id}/firmware/{platform}/release    { actor }
/// GET    internal/drivers/models/{id}/firmware/{platform}/versions
/// PUT    internal/drivers/models/{id}/firmware/{platform}/rollback   { version, until, actor }
/// DELETE internal/drivers/models/{id}/firmware/{platform}/rollback?changedBy=
/// GET    internal/sessions/{id}/firmware                             204 when the session gets no BIOS update, 410 when its file is gone
/// </summary>
public sealed partial class DriverFirmwareFunctions
{
    private const string Base = "internal/drivers/models/{id}/firmware";

    private readonly DriverFirmwareService _firmware;
    private readonly DeviceSessionRepository _sessions;

    /// <param name="firmware">BIOS updates.</param>
    /// <param name="sessions">Device sessions.</param>
    public DriverFirmwareFunctions(DriverFirmwareService firmware, DeviceSessionRepository sessions)
    {
        _firmware = firmware;
        _sessions = sessions;
    }

    /// <summary>GET internal/drivers/models/{id}/firmware.</summary>
    [Function("GetDriverFirmware")]
    public async Task<HttpResponseData> GetFirmware(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Base)] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(id, out var modelId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", ct);
        }

        return await _firmware.GetPageAsync(modelId, ct) is { } page
            ? await DriverHttp.JsonAsync(req, HttpStatusCode.OK, page, ct)
            : await DriverHttp.ProblemAsync(req, HttpStatusCode.NotFound, "Driver model not found.", ct);
    }

    /// <summary>PUT .../firmware/{platform}/action.</summary>
    [Function("SetDriverFirmwareAction")]
    public async Task<HttpResponseData> SetAction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = Base + "/{platform}/action")] HttpRequestData req,
        string id,
        string platform,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (await InvalidAsync(req, id, platform, ct) is { } problem)
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverFirmwareActionRequest>(req, ct);
        return await DriverHttp.ResultAsync(req, await _firmware.SetActionAsync(Guid.Parse(id), platform, request, ct), ct);
    }

    /// <summary>POST .../firmware/{platform}/release.</summary>
    [Function("ReleaseDriverFirmware")]
    public async Task<HttpResponseData> Release(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = Base + "/{platform}/release")] HttpRequestData req,
        string id,
        string platform,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (await InvalidAsync(req, id, platform, ct) is { } problem)
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, ct);
        return await DriverHttp.ResultAsync(req, await _firmware.ReleaseAsync(Guid.Parse(id), platform, request, ct), ct);
    }

    /// <summary>GET .../firmware/{platform}/versions.</summary>
    [Function("GetDriverFirmwareVersions")]
    public async Task<HttpResponseData> Versions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = Base + "/{platform}/versions")] HttpRequestData req,
        string id,
        string platform,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (await InvalidAsync(req, id, platform, ct) is { } problem)
        {
            return problem;
        }

        return await DriverHttp.ResultAsync(req, await _firmware.VersionsAsync(Guid.Parse(id), platform, ct), ct);
    }

    /// <summary>PUT .../firmware/{platform}/rollback.</summary>
    [Function("SetDriverFirmwareRollBack")]
    public async Task<HttpResponseData> RollBack(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = Base + "/{platform}/rollback")] HttpRequestData req,
        string id,
        string platform,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (await InvalidAsync(req, id, platform, ct) is { } problem)
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverComponentRollBackRequest>(req, ct);
        return await DriverHttp.ResultAsync(req, await _firmware.RollBackAsync(Guid.Parse(id), platform, request, ct), ct);
    }

    /// <summary>DELETE .../firmware/{platform}/rollback?changedBy=.</summary>
    [Function("ClearDriverFirmwareRollBack")]
    public async Task<HttpResponseData> UndoRollBack(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = Base + "/{platform}/rollback")] HttpRequestData req,
        string id,
        string platform,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (await InvalidAsync(req, id, platform, ct) is { } problem)
        {
            return problem;
        }

        return await DriverHttp.ResultAsync(req, await _firmware.UndoRollBackAsync(Guid.Parse(id), platform, DriverHttp.ChangedBy(req), ct), ct);
    }

    /// <summary>GET internal/sessions/{id}/firmware.</summary>
    [Function("GetSessionFirmware")]
    public async Task<HttpResponseData> GetSessionFirmware(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions/{id}/firmware")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(id, out var sessionId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var session = await _sessions.GetByIdAsync(sessionId, ct);
        if (session is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        if (session.State is not (SessionState.SessionStarted or SessionState.SessionInProgress))
        {
            return req.CreateResponse(HttpStatusCode.Conflict);
        }

        if (session.FirmwareVersion is null)
        {
            return req.CreateResponse(HttpStatusCode.NoContent);
        }

        // 410: the session was given a BIOS whose file is no longer stored, so the device reports it as not updated.
        return await _firmware.DescriptorAsync(session, ct) is { } descriptor
            ? await DriverHttp.JsonAsync(req, HttpStatusCode.OK, descriptor, ct)
            : req.CreateResponse(HttpStatusCode.Gone);
    }

    private static async Task<HttpResponseData?> InvalidAsync(HttpRequestData req, string id, string platform, CancellationToken ct) =>
        !Guid.TryParse(id, out _) ? await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", ct)
        : !PlatformPattern().IsMatch(platform) ? await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid platform.", ct)
        : null;

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9]{1,16}$")]
    private static partial System.Text.RegularExpressions.Regex PlatformPattern();
}
