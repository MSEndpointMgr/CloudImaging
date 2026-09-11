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
/// OS image catalog CRUD endpoints (T083, FR-036, FR-037).
///
/// GET    /api/internal/images           — list active images
/// GET    /api/internal/images/{id}      — get single image
/// POST   /api/internal/images           — create/register new image
/// PATCH  /api/internal/images/{id}      — update metadata
/// DELETE /api/internal/images/{id}      — delete (blocked if image has an active session)
/// </summary>
public sealed partial class ImageCatalogFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OsImageRepository _imageRepo;
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly ImageDeletionGuardService _deletionGuard;
    private readonly ILogger<ImageCatalogFunctions> _logger;

    public ImageCatalogFunctions(
        OsImageRepository imageRepo,
        DeviceSessionRepository sessionRepo,
        ImageDeletionGuardService deletionGuard,
        ILogger<ImageCatalogFunctions> logger)
    {
        _imageRepo = imageRepo;
        _sessionRepo = sessionRepo;
        _deletionGuard = deletionGuard;
        _logger = logger;
    }

    // ── GET /api/internal/images ─────────────────────────────────────────────

    [Function("GetImages")]
    public async Task<HttpResponseData> GetImages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/images")] HttpRequestData req,
        FunctionContext context)
    {
        var images = await _imageRepo.ListActiveAsync(context.CancellationToken).ToListAsync();
        var inUseImageIds = await _deletionGuard.GetInUseImageIdsAsync(context.CancellationToken);
        var withComputedInUse = images.Select(i => WithIsInUse(i, inUseImageIds.Contains(i.ImageId)));

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(withComputedInUse, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── GET /api/internal/images/{id} ────────────────────────────────────────

    [Function("GetImageById")]
    public async Task<HttpResponseData> GetImageById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/images/{id}")] HttpRequestData req,
        string id, FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var image = await _imageRepo.GetByIdAsync(imageId, context.CancellationToken);
        if (image is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var canDelete = await _deletionGuard.CanDeleteAsync(imageId, context.CancellationToken);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(WithIsInUse(image, !canDelete), JsonOptions),
            context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/images ────────────────────────────────────────────

    [Function("CreateImage")]
    public async Task<HttpResponseData> CreateImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/images")] HttpRequestData req,
        FunctionContext context)
    {
        OsImage? image;
        try { image = await JsonSerializer.DeserializeAsync<OsImage>(req.Body, JsonOptions, context.CancellationToken); }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        if (image is null)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var activeCount = await _imageRepo.GetActiveCountAsync(context.CancellationToken);
        if (activeCount >= OsImageRepository.MaxActiveEntries)
        {
            var full = req.CreateResponse(HttpStatusCode.Conflict);
            await full.WriteStringAsync(
                $"OS image catalog is at capacity ({OsImageRepository.MaxActiveEntries}). Remove an unused image before adding another.",
                context.CancellationToken);
            return full;
        }

        var newImage = new OsImage
        {
            ImageId = image.ImageId == Guid.Empty ? Guid.NewGuid() : image.ImageId,
            Name = image.Name,
            Version = image.Version,
            Description = image.Description,
            SizeBytes = image.SizeBytes,
            StoragePath = image.StoragePath,
            Sha256Hash = image.Sha256Hash,
            UploadedAt = image.UploadedAt == default ? DateTimeOffset.UtcNow : image.UploadedAt,
        };

        await _imageRepo.CreateAsync(newImage, context.CancellationToken);
        LogImageCreated(_logger, newImage.ImageId, newImage.Name);

        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(newImage, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── PATCH /api/internal/images/{id} ──────────────────────────────────────

    [Function("UpdateImage")]
    public async Task<HttpResponseData> UpdateImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "internal/images/{id}")] HttpRequestData req,
        string id, FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var existing = await _imageRepo.GetByIdAsync(imageId, context.CancellationToken);
        if (existing is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);

        var updated = new OsImage
        {
            ImageId = existing.ImageId,
            Name = body.RootElement.TryGetProperty("name", out var n) ? n.GetString()! : existing.Name,
            Version = body.RootElement.TryGetProperty("version", out var v) ? v.GetString()! : existing.Version,
            Description = body.RootElement.TryGetProperty("description", out var d) ? d.GetString() : existing.Description,
            SizeBytes = existing.SizeBytes,
            StoragePath = existing.StoragePath,
            Sha256Hash = existing.Sha256Hash,
            UploadedAt = existing.UploadedAt,
            IsInUse = existing.IsInUse,
        };

        await _imageRepo.UpdateAsync(updated, context.CancellationToken);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(updated, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── DELETE /api/internal/images/{id} ─────────────────────────────────────

    [Function("DeleteImage")]
    public async Task<HttpResponseData> DeleteImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/images/{id}")] HttpRequestData req,
        string id, FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var image = await _imageRepo.GetByIdAsync(imageId, context.CancellationToken);
        if (image is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        if (!await _deletionGuard.CanDeleteAsync(imageId, context.CancellationToken))
        {
            LogDeleteBlockedInUse(_logger, imageId);
            var conflict = req.CreateResponse(HttpStatusCode.Conflict);
            await conflict.WriteStringAsync(
                "Image cannot be deleted while it is assigned to an active session.",
                context.CancellationToken);
            return conflict;
        }

        await _imageRepo.DeleteAsync(imageId, context.CancellationToken);
        LogImageDeleted(_logger, imageId);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    /// <summary>Returns a copy of <paramref name="image"/> with <c>IsInUse</c> set to <paramref name="isInUse"/>
    /// (the stored value is never authoritative — it is always computed from active sessions at read time).</summary>
    private static OsImage WithIsInUse(OsImage image, bool isInUse) => new()
    {
        ImageId = image.ImageId,
        Name = image.Name,
        Version = image.Version,
        Description = image.Description,
        SizeBytes = image.SizeBytes,
        StoragePath = image.StoragePath,
        UploadedAt = image.UploadedAt,
        IsInUse = isInUse,
        Sha256Hash = image.Sha256Hash,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Image {ImageId} created: {Name}.")]
    private static partial void LogImageCreated(ILogger logger, Guid imageId, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Delete blocked for image {ImageId} — image is in use.")]
    private static partial void LogDeleteBlockedInUse(ILogger logger, Guid imageId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Image {ImageId} deleted.")]
    private static partial void LogImageDeleted(ILogger logger, Guid imageId);
}
