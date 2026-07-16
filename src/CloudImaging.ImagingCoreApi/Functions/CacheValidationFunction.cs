using System.Net;
using System.Text.Json;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST /api/internal/sessions/{sessionId}/cache/validate — Validates a client-provided SHA-256
/// hash against the authoritative hash of the session's assigned OS image (T056b, FR-009d).
///
/// Returns: { "valid": true|false }
/// </summary>
public sealed partial class CacheValidationFunction
{
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly OsImageRepository _imageRepo;
    private readonly ILogger<CacheValidationFunction> _logger;

    public CacheValidationFunction(
        DeviceSessionRepository sessionRepo,
        OsImageRepository imageRepo,
        ILogger<CacheValidationFunction> logger)
    {
        _sessionRepo = sessionRepo;
        _imageRepo   = imageRepo;
        _logger      = logger;
    }

    [Function(nameof(CacheValidationFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{sessionId}/cache/validate")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("sha256Hash", out var hashProp))
            return req.CreateResponse(HttpStatusCode.BadRequest);

        var clientHash = hashProp.GetString() ?? string.Empty;

        var session = await _sessionRepo.GetByIdAsync(sessionGuid, context.CancellationToken);
        if (session?.AssignedOsImageId is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            return notFound;
        }

        var image = await _imageRepo.GetByIdAsync(session.AssignedOsImageId.Value, context.CancellationToken);
        bool valid = image is not null
            && string.Equals(image.Sha256Hash, clientHash, StringComparison.OrdinalIgnoreCase);

        LogValidationResult(_logger, sessionGuid, valid);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(new { valid }),
            context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Cache validation for session {SessionId}: valid={Valid}.")]
    private static partial void LogValidationResult(ILogger logger, Guid sessionId, bool valid);
}
