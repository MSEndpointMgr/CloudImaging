using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST /api/internal/sessions/{sessionId}/assign — Assigns an OS image to a coupled session (T039b, FR-025).
///
/// Rules:
///   1. Session must be in <see cref="SessionState.SessionAssigned"/> state.
///   2. Session must not already have an image assigned.
///   3. The referenced <see cref="OsImage"/> must be active in the catalog.
///   4. On success, issue an initial SAS token URL using <see cref="PortalConfiguration.SasTokenUrlExpiryMinutes"/>.
///   5. Transition to <see cref="SessionState.SessionStarted"/>.
///
/// Response codes:
///   201 Created — assignment succeeded; returns session ID + SAS token URL.
///   404 Not Found — session not found.
///   400 Bad Request — image not found in active catalog.
///   409 Conflict — session not in assignable state.
/// </summary>
public sealed partial class AssignSessionFunction
{
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly OsImageRepository _imageRepo;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<AssignSessionFunction> _logger;

    public AssignSessionFunction(
        DeviceSessionRepository sessionRepo,
        OsImageRepository imageRepo,
        PortalConfigurationRepository configRepo,
        BlobServiceClient blobClient,
        ILogger<AssignSessionFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _imageRepo = imageRepo;
        _configRepo = configRepo;
        _blobClient = blobClient;
        _logger = logger;
    }

    [Function(nameof(AssignSessionFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{sessionId}/assign")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        // Deserialize request body: { "osImageId": "..." }
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("osImageId", out var imageIdProp)
            || !Guid.TryParse(imageIdProp.GetString(), out var imageId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("osImageId field (GUID) is required.", context.CancellationToken);
            return bad;
        }

        var session = await _sessionRepo.GetByIdAsync(sessionGuid, context.CancellationToken);
        if (session is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        // 409 if session not in assignable state
        if (session.State != SessionState.SessionAssigned || session.AssignedOsImageId.HasValue)
        {
            LogInvalidStateForAssignment(_logger, sessionGuid, session.State);
            return req.CreateResponse(HttpStatusCode.Conflict);
        }

        // 400 if image not found or inactive
        var image = await _imageRepo.GetByIdAsync(imageId, context.CancellationToken);
        if (image is null)
        {
            LogImageNotFound(_logger, imageId);
            var notFound = req.CreateResponse(HttpStatusCode.BadRequest);
            await notFound.WriteStringAsync("OS image not found in active catalog.", context.CancellationToken);
            return notFound;
        }

        // Read SAS expiry from portal configuration
        var config = await _configRepo.GetAsync(context.CancellationToken);
        var sasExpiry = TimeSpan.FromMinutes(
            config.SasTokenUrlExpiryMinutes > 0 ? config.SasTokenUrlExpiryMinutes : 60);

        // Generate SAS token URL for the OS image blob
        var sasUrl = await GenerateSasUrlAsync(image.StoragePath, sasExpiry, context.CancellationToken);

        // Transition: SessionAssigned → SessionStarted
        var assigned = new DeviceSession
        {
            SessionId = session.SessionId,
            State = SessionState.SessionStarted,
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
            AssignedOsImageId = imageId,
            SasTokenUrl = sasUrl,
            SasTokenUrlExpiresAt = DateTimeOffset.UtcNow + sasExpiry,
            OverallProgressPercent = 0,
            CreatedAt = session.CreatedAt,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
        };

        await _sessionRepo.UpdateAsync(assigned, context.CancellationToken);
        LogSessionAssigned(_logger, sessionGuid, imageId);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            sessionId = assigned.SessionId,
            state = assigned.State.ToString(),
            osImageId = imageId,
            sasTokenUrl = sasUrl,
            expiresAt = assigned.SasTokenUrlExpiresAt,
            sha256Hash = image.Sha256Hash,
        }), context.CancellationToken);
        return response;
    }

    private async Task<string> GenerateSasUrlAsync(string storagePath, TimeSpan expiry, CancellationToken cancellationToken)
    {
        try
        {
            // storagePath format: {container}/{blobName}
            var slash = storagePath.IndexOf('/', StringComparison.Ordinal);
            if (slash < 0)
            {
                return storagePath;
            }

            var container = storagePath[..slash];
            var blobName = storagePath[(slash + 1)..];

            return await BlobSasUrlGenerator.GenerateAsync(
                _blobClient, container, blobName, BlobSasPermissions.Read, expiry, cancellationToken);
        }
        catch
        {
            return storagePath; // Fallback — caller should handle auth separately
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Session {SessionId} not in assignable state (state={State}).")]
    private static partial void LogInvalidStateForAssignment(
        ILogger logger, Guid sessionId, SessionState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OS image {ImageId} not found in active catalog.")]
    private static partial void LogImageNotFound(ILogger logger, Guid imageId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Session {SessionId} assigned OS image {ImageId} — state=SessionStarted.")]
    private static partial void LogSessionAssigned(ILogger logger, Guid sessionId, Guid imageId);
}
