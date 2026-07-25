using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CloudImaging.ImagingCoreApi.Repositories;
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
        var sasExpiry = TimeSpan.FromMinutes(config.SasTokenUrlExpiryMinutes > 0 ? config.SasTokenUrlExpiryMinutes : 60);

        // Only refresh if < 15 minutes remain on the current token
        bool needsRefresh = !session.SasTokenUrlExpiresAt.HasValue
            || (session.SasTokenUrlExpiresAt.Value - DateTimeOffset.UtcNow) < RefreshThreshold;

        string sasUrl = session.SasTokenUrl ?? string.Empty;

        if (needsRefresh)
        {
            var image = await _imageRepo.GetByIdAsync(session.AssignedOsImageId.Value, context.CancellationToken);
            if (image is not null)
            {
                sasUrl = GenerateSasUrl(_blobClient, image.StoragePath, sasExpiry);
            }

            var updated = new CloudImaging.Contracts.Models.DeviceSession
            {
                SessionId = session.SessionId,
                State = session.State,
                DeviceSerialNumber = session.DeviceSerialNumber,
                DeviceManufacturer = session.DeviceManufacturer,
                DeviceModel = session.DeviceModel,
                HardwareMetadata = session.HardwareMetadata,
                PreFlightAuthorizationResult = session.PreFlightAuthorizationResult,
                Passcode = session.Passcode,
                PasscodeExpiresAt = session.PasscodeExpiresAt,
                PasscodeConsumed = session.PasscodeConsumed,
                DeviceSessionToken = session.DeviceSessionToken,
                DeviceSessionTokenExpiresAt = session.DeviceSessionTokenExpiresAt,
                AssignedOsImageId = session.AssignedOsImageId,
                SasTokenUrl = sasUrl,
                SasTokenUrlExpiresAt = DateTimeOffset.UtcNow + sasExpiry,
                OverallProgressPercent = session.OverallProgressPercent,
                CurrentStep = session.CurrentStep,
                CreatedAt = session.CreatedAt,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
            };

            await _sessionRepo.UpdateAsync(updated, context.CancellationToken);
            LogSasRefreshed(_logger, sessionGuid);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { sasTokenUrl = sasUrl }),
            context.CancellationToken);
        return response;
    }

    private static string GenerateSasUrl(BlobServiceClient blobClient, string storagePath, TimeSpan expiry)
    {
        var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return storagePath;
        }

        var container = storagePath[..slash];
        var blobName = storagePath[(slash + 1)..];
        var blobRef = blobClient.GetBlobContainerClient(container).GetBlobClient(blobName);
        if (!blobRef.CanGenerateSasUri)
        {
            return blobRef.Uri.ToString();
        }

        var builder = new BlobSasBuilder { BlobContainerName = container, BlobName = blobName, Resource = "b", ExpiresOn = DateTimeOffset.UtcNow + expiry };
        builder.SetPermissions(BlobSasPermissions.Read);
        return blobRef.GenerateSasUri(builder).ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SAS token refreshed for session {SessionId}.")]
    private static partial void LogSasRefreshed(ILogger logger, Guid sessionId);
}
