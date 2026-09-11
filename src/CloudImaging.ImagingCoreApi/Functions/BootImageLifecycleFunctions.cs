using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Boot image lifecycle CRUD endpoints (T115, FR-063).
///
/// POST   /api/internal/boot-images/publish           — publish a new boot image (enforces 5-entry limit)
/// DELETE /api/internal/boot-images/{id}              — soft-delete (deactivate) a boot image
/// PATCH  /api/internal/boot-images/{id}/demote       — demote from latest-published
/// GET    /api/internal/boot-images/{id}/active-cert  — returns active cert thumbprint for the boot image
/// </summary>
public sealed partial class BootImageLifecycleFunctions
{
    private static readonly System.Text.Json.JsonSerializerOptions CachedJsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly BootImageRepository _repo;
    private readonly ILogger<BootImageLifecycleFunctions> _logger;

    public BootImageLifecycleFunctions(
        BootImageRepository repo,
        ILogger<BootImageLifecycleFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    // ── POST /api/internal/boot-images/publish ────────────────────────────────

    [Function("PublishBootImage")]
    public async Task<HttpResponseData> PublishBootImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/boot-images/publish")] HttpRequestData req,
        FunctionContext context)
    {
        BootImage? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<BootImage>(
                req.Body,
                CachedJsonOptions,
                cancellationToken: context.CancellationToken);
        }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        if (payload is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var toPublish = payload.BootImageId == Guid.Empty
            ? new BootImage
            {
                BootImageId = Guid.NewGuid(),
                Version = TextNormalization.NormalizeLookalikes(payload.Version) ?? payload.Version,
                CreatedAt = payload.CreatedAt == default ? DateTimeOffset.UtcNow : payload.CreatedAt,
                SizeBytes = payload.SizeBytes,
                StoragePath = payload.StoragePath,
                ManifestVersion = payload.ManifestVersion,
                Sha256Hash = payload.Sha256Hash,
            }
            : payload;

        var published = await _repo.PublishAsync(toPublish, context.CancellationToken);

        LogPublished(_logger, published.BootImageId, published.Version);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(published),
            context.CancellationToken);
        return response;
    }

    // ── DELETE /api/internal/boot-images/{id} ────────────────────────────────

    [Function("DeleteBootImage")]
    public async Task<HttpResponseData> DeleteBootImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/boot-images/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var image = await _repo.GetByIdAsync(imageId, context.CancellationToken);
        if (image is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        if (image.IsLatestPublished)
        {
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteStringAsync(
                "Cannot delete the currently published boot image. Publish a replacement first.",
                context.CancellationToken);
            return conflict;
        }

        await _repo.DeleteAsync(imageId, context.CancellationToken);
        LogDeleted(_logger, imageId);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Boot image published: {BootImageId} v{Version}.")]
    private static partial void LogPublished(ILogger logger, Guid bootImageId, string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image deleted: {BootImageId}.")]
    private static partial void LogDeleted(ILogger logger, Guid bootImageId);
}
