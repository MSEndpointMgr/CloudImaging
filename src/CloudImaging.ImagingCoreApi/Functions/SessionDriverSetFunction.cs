using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// The driver set pinned on a session, called only by the Device Gateway (contracts/device-gateway.md).
///
/// GET  internal/sessions/{id}/driver-set                               descriptor with a read link per component
/// POST internal/sessions/{id}/driver-set/components/{index}/link       a new read link for one component
///
/// Read links are user-delegation SAS for exactly the component's blob, valid for 60 minutes.
/// Whether a component is critical comes from the categories pinned on the session at
/// assignment, not from the set. The set document and its files stay while the session is
/// active, even after its model is removed.
/// </summary>
public sealed partial class SessionDriverSetFunction
{
    private readonly DeviceSessionRepository _sessions;
    private readonly DriverSetDocumentRepository _sets;
    private readonly DriverStorageService _storage;
    private readonly ILogger<SessionDriverSetFunction> _logger;

    /// <param name="sessions">Device sessions.</param>
    /// <param name="sets">Set documents.</param>
    /// <param name="storage">Read links.</param>
    /// <param name="logger">Logger.</param>
    public SessionDriverSetFunction(DeviceSessionRepository sessions, DriverSetDocumentRepository sets, DriverStorageService storage, ILogger<SessionDriverSetFunction> logger)
    {
        _sessions = sessions;
        _sets = sets;
        _storage = storage;
        _logger = logger;
    }

    /// <summary>GET internal/sessions/{id}/driver-set.</summary>
    [Function("GetSessionDriverSet")]
    public async Task<HttpResponseData> GetDriverSet(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions/{id}/driver-set")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var (status, session) = await LoadAsync(id, ct);
        if (session is null)
        {
            return req.CreateResponse(status);
        }

        if (session.DriverSkipReason is { } skip)
        {
            return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, new SessionDriverSetDescriptor
            {
                SkipReason = skip,
                MatchKey = session.DriverMatchKey,
                ModelName = session.DriverModelName,
                Release = session.DriverRelease,
            }, ct);
        }

        var document = await PinnedSetAsync(session, ct);
        if (document is null)
        {
            return req.CreateResponse(session.DriverSetVersion is null ? HttpStatusCode.NotFound : HttpStatusCode.InternalServerError);
        }

        var critical = CriticalCategories(session);
        if (critical is null)
        {
            return req.CreateResponse(HttpStatusCode.InternalServerError);
        }

        var components = new List<SessionDriverComponent>();
        for (var index = 0; index < document.Items.Count; index++)
        {
            var item = document.Items[index];
            var (url, expiresAt) = await _storage.GetReadLinkAsync(item.BlobName, ct);
            components.Add(new SessionDriverComponent
            {
                Index = index,
                Name = item.Name,
                ComponentId = item.ComponentId,
                Version = item.Version,
                Origin = item.Origin,
                Category = item.Category,
                Action = item.Action,
                Critical = item.Category is { } category && critical.Contains(category),
                SizeBytes = item.Size,
                Sha256 = item.Sha256,
                FileName = item.FileName,
                DownloadUrl = url,
                DownloadUrlExpiresAt = expiresAt,
                Extract = item.Extract,
                InstallCommand = item.InstallCommand,
                PciIds = item.PciIds,
            });
        }

        LogDescriptorServed(_logger, session.SessionId, document.ModelId, document.Version, components.Count);
        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, new SessionDriverSetDescriptor
        {
            MatchKey = session.DriverMatchKey,
            ModelName = session.DriverModelName,
            Release = session.DriverRelease,
            SetVersion = session.DriverSetVersion,
            Components = components,
        }, ct);
    }

    /// <summary>POST internal/sessions/{id}/driver-set/components/{index}/link.</summary>
    [Function("RefreshSessionDriverComponentLink")]
    public async Task<HttpResponseData> RefreshLink(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{id}/driver-set/components/{index}/link")] HttpRequestData req,
        string id,
        string index,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var (status, session) = await LoadAsync(id, ct);
        if (session is null)
        {
            return req.CreateResponse(status);
        }

        var document = await PinnedSetAsync(session, ct);
        if (!int.TryParse(index, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var position)
            || document is null
            || position >= document.Items.Count)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "The component is not in the session's driver set.", ct);
        }

        var (url, expiresAt) = await _storage.GetReadLinkAsync(document.Items[position].BlobName, ct);
        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, new SessionDriverComponentLink { DownloadUrl = url, DownloadUrlExpiresAt = expiresAt }, ct);
    }

    // 404 for an unknown session, 409 outside SessionStarted and SessionInProgress, and 404 for a
    // session assigned before driver management (it has no pin at all: no driver work).
    private async Task<(HttpStatusCode Status, DeviceSession? Session)> LoadAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var sessionId))
        {
            return (HttpStatusCode.BadRequest, null);
        }

        var session = await _sessions.GetByIdAsync(sessionId, ct);
        if (session is null)
        {
            return (HttpStatusCode.NotFound, null);
        }

        if (session.State is not (SessionState.SessionStarted or SessionState.SessionInProgress))
        {
            return (HttpStatusCode.Conflict, null);
        }

        return session.DriverSkipReason is null && session.DriverSetVersion is null
            ? (HttpStatusCode.NotFound, null)
            : (HttpStatusCode.OK, session);
    }

    private async Task<DriverSetDocument?> PinnedSetAsync(DeviceSession session, CancellationToken ct)
    {
        if (session is not { DriverModelId: { } modelId, DriverRelease: { } release, DriverSetVersion: { } version })
        {
            return null;
        }

        var document = await _sets.GetAsync(modelId, release, version, ct);
        if (document is null)
        {
            LogPinnedSetMissing(_logger, session.SessionId, modelId, version);
        }

        return document;
    }

    // Null when the pin cannot be read: serving every component as non-critical would hide failures that must stop the deployment.
    private HashSet<string>? CriticalCategories(DeviceSession session)
    {
        try
        {
            var categories = session.DriverCriticalCategories is { Length: > 0 } json ? JsonSerializer.Deserialize<List<string>>(json) : null;
            return new HashSet<string>(categories ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            LogCriticalCategoriesUnreadable(_logger, session.SessionId, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Critical driver categories pinned on session {SessionId} cannot be read: {Reason}")]
    private static partial void LogCriticalCategoriesUnreadable(ILogger logger, Guid sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Driver set {Version} of model {ModelId} served to session {SessionId} with {Count} components.")]
    private static partial void LogDescriptorServed(ILogger logger, Guid sessionId, Guid modelId, int version, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Driver set {Version} of model {ModelId} pinned on session {SessionId} is missing.")]
    private static partial void LogPinnedSetMissing(ILogger logger, Guid sessionId, Guid modelId, int version);
}
