using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST /api/internal/sessions/{sessionId}/sas/refresh — Re-issues the SAS token URL
/// before expiry so the in-progress download is not interrupted (T049, FR-025).
///
/// Refreshes if the existing SAS token has &lt; 15 minutes remaining.
/// Returns 200 with the new (or unchanged) URL.
/// </summary>
public sealed partial class RefreshSasTokenFunction
{
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromMinutes(15);

    private readonly DeviceSessionRepository _sessionRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly OsImageRepository _imageRepo;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<RefreshSasTokenFunction> _logger;

    /// <param name="sessionRepo">Device session repository.</param>
    /// <param name="configRepo">Portal configuration repository.</param>
    /// <param name="imageRepo">OS image catalog repository.</param>
    /// <param name="blobClient">Blob service client used to reissue the assigned image's download SAS URL.</param>
    /// <param name="logger">Logger for this function.</param>
    public RefreshSasTokenFunction(
        DeviceSessionRepository sessionRepo,
        PortalConfigurationRepository configRepo,
        OsImageRepository imageRepo,
        BlobServiceClient blobClient,
        ILogger<RefreshSasTokenFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _configRepo = configRepo;
        _imageRepo = imageRepo;
        _blobClient = blobClient;
        _logger = logger;
    }

    /// <summary>POST internal/sessions/{sessionId}/sas/refresh. Reissues a fresh SAS token URL for a session's assigned image.</summary>
    [Function(nameof(RefreshSasTokenFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{sessionId}/sas/refresh")] HttpRequestData req,
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

        if (!session.AssignedOsImageId.HasValue)
        {
            return req.CreateResponse(HttpStatusCode.Conflict);
        }

        var config = await _configRepo.GetAsync(context.CancellationToken);
        var sasExpiry = TimeSpan.FromMinutes(config.SasTokenUrlExpiryMinutes > 0 ? config.SasTokenUrlExpiryMinutes : 240);

        // Only refresh if < 15 minutes remain on the current token
        bool needsRefresh = !session.SasTokenUrlExpiresAt.HasValue
            || (session.SasTokenUrlExpiresAt.Value - DateTimeOffset.UtcNow) < RefreshThreshold;

        string sasUrl = session.SasTokenUrl ?? string.Empty;
        DateTimeOffset? sasTokenUrlExpiresAt = session.SasTokenUrlExpiresAt;

        if (needsRefresh)
        {
            var image = await _imageRepo.GetByIdAsync(session.AssignedOsImageId.Value, context.CancellationToken);
            // Never hand out a download URL for an image the device cannot run (e.g. an assignment made before enforcement).
            if (image is not null && image.Architecture != session.Architecture)
            {
                return await ArchitectureCompatibility.ConflictAsync(
                    req, new ArchitectureMismatchException(image.Architecture, [(sessionGuid, session.Architecture)]), context.CancellationToken);
            }

            if (image is not null)
            {
                sasUrl = await GenerateSasUrlAsync(_blobClient, image.StoragePath, sasExpiry, context.CancellationToken);
            }

            sasTokenUrlExpiresAt = DateTimeOffset.UtcNow + sasExpiry;

            var updated = session with
            {
                SasTokenUrl = sasUrl,
                SasTokenUrlExpiresAt = sasTokenUrlExpiresAt,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
            };

            await _sessionRepo.UpdateAsync(updated, context.CancellationToken);
            LogSasRefreshed(_logger, sessionGuid);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(new { sasTokenUrl = sasUrl, sasTokenUrlExpiresAt }),
            context.CancellationToken);
        return response;
    }

    private static async Task<string> GenerateSasUrlAsync(BlobServiceClient blobClient, string storagePath, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return storagePath;
        }

        var container = storagePath[..slash];
        var blobName = storagePath[(slash + 1)..];
        return await BlobSasUrlGenerator.GenerateAsync(
            blobClient, container, blobName, BlobSasPermissions.Read, expiry, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SAS token refreshed for session {SessionId}.")]
    private static partial void LogSasRefreshed(ILogger logger, Guid sessionId);
}
