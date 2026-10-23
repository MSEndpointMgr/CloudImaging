using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// GET internal/sessions/{id}/setup-profile. Called only by the Device Gateway: returns the
/// setup profile version pinned on the session with a short-lived read URL for exactly that
/// version document.
/// </summary>
public sealed partial class SessionSetupProfileFunction
{
    /// <summary>Lifetime of the read URL.</summary>
    public static readonly TimeSpan ContentUrlLifetime = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeviceSessionRepository _sessions;
    private readonly SetupProfileRepository _profiles;
    private readonly SetupProfileVersionRepository _versions;
    private readonly BlobServiceClient _blobService;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionSetupProfileFunction> _logger;

    /// <param name="sessions">Device sessions.</param>
    /// <param name="profiles">Profile rows, including tombstones.</param>
    /// <param name="versions">Version rows and documents.</param>
    /// <param name="blobService">Blob service used to sign the read URL.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    public SessionSetupProfileFunction(
        DeviceSessionRepository sessions,
        SetupProfileRepository profiles,
        SetupProfileVersionRepository versions,
        BlobServiceClient blobService,
        TimeProvider time,
        ILogger<SessionSetupProfileFunction> logger)
    {
        _sessions = sessions;
        _profiles = profiles;
        _versions = versions;
        _blobService = blobService;
        _time = time;
        _logger = logger;
    }

    /// <summary>GET internal/sessions/{id}/setup-profile.</summary>
    [Function("GetSessionSetupProfile")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions/{id}/setup-profile")] HttpRequestData req,
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
        if (session.SetupProfileId is not { } profileId || session.SetupProfileVersion is not { } version)
        {
            return req.CreateResponse(HttpStatusCode.NoContent);
        }

        var row = await _versions.GetAsync(profileId, version, ct);
        if (row is null || !await _versions.BlobExistsAsync(row.BlobName, ct))
        {
            LogPinnedVersionMissing(_logger, sessionId, profileId, version);
            return req.CreateResponse(HttpStatusCode.InternalServerError);
        }

        var profile = await _profiles.GetAsync(profileId, ct);
        var content = await _versions.DownloadAsync(row.BlobName, ct);
        var url = await BlobSasUrlGenerator.GenerateAsync(_blobService, SetupProfileVersionRepository.ContainerName, row.BlobName, BlobSasPermissions.Read, ContentUrlLifetime, ct);
        var descriptor = new SetupProfileDescriptor
        {
            ProfileId = profileId,
            ProfileName = session.SetupProfileName ?? profile?.Name ?? string.Empty,
            Version = version,
            FailureHandling = content?.FailureHandling ?? SetupFailureHandling.CompleteWithWarnings,
            ContentUrl = url,
            ContentUrlExpiresAt = _time.GetUtcNow() + ContentUrlLifetime,
            ContentSha256 = row.ContentSha256,
            ContentSizeBytes = row.ContentSizeBytes,
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(descriptor, JsonOptions), ct);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Session {SessionId} pins setup profile {ProfileId} version {Version}, but the version is missing.")]
    private static partial void LogPinnedVersionMissing(ILogger logger, Guid sessionId, Guid profileId, int version);
}
