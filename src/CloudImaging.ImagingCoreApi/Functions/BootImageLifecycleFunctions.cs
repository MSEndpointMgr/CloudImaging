using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Boot image lifecycle CRUD endpoints (T115, FR-063).
///
/// DELETE /api/internal/boot-images/{id}: soft-delete (deactivate) a boot image
/// POST   /api/internal/boot-images/{id}/promote: move a tested pre-production image to
///        production and make it the latest for its architecture
/// POST   /api/internal/boot-images/{id}/demote: move a production image back to pre-production,
///        restoring the previously promoted image as latest when needed
///
/// Publishing lives in BootImageUploadFunctions, which only writes a catalog entry for a blob
/// it staged and verified itself. Accepting a caller-supplied storage path here would let the
/// catalog point at unverified content.
/// </summary>
public sealed partial class BootImageLifecycleFunctions
{
    private readonly BootImageRepository _repo;
    private readonly ILogger<BootImageLifecycleFunctions> _logger;

    public BootImageLifecycleFunctions(
        BootImageRepository repo,
        ILogger<BootImageLifecycleFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
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
                "Cannot delete the latest production boot image. Promote a replacement first.",
                context.CancellationToken);
            return conflict;
        }

        await _repo.DeleteAsync(imageId, context.CancellationToken);
        LogDeleted(_logger, imageId);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    // ── POST /api/internal/boot-images/{id}/promote ────────────────────────────

    [Function("PromoteBootImage")]
    public async Task<HttpResponseData> PromoteBootImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/boot-images/{id}/promote")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var promoted = await _repo.PromoteAsync(imageId, context.CancellationToken);
        if (promoted is null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Boot image not found, or it was removed from the catalog.", context.CancellationToken);
            return notFound;
        }

        LogPromoted(_logger, imageId, promoted.Version, promoted.Architecture);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            bootImageId = promoted.BootImageId,
            version = promoted.Version,
            architecture = MachineArchitecturePlatform.Slug(promoted.Architecture),
            isProduction = promoted.IsProduction,
            isLatestPublished = promoted.IsLatestPublished,
        }), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/boot-images/{id}/demote ─────────────────────────────

    [Function("DemoteBootImage")]
    public async Task<HttpResponseData> DemoteBootImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/boot-images/{id}/demote")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var imageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var result = await _repo.DemoteAsync(imageId, context.CancellationToken);
        if (result is not { } outcome)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Boot image not found, or it was removed from the catalog.", context.CancellationToken);
            return notFound;
        }

        LogDemoted(_logger, imageId, outcome.Demoted.Version, outcome.RestoredLatest?.Version);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            bootImageId = outcome.Demoted.BootImageId,
            version = outcome.Demoted.Version,
            architecture = MachineArchitecturePlatform.Slug(outcome.Demoted.Architecture),
            isProduction = outcome.Demoted.IsProduction,
            isLatestPublished = outcome.Demoted.IsLatestPublished,
            restoredLatest = outcome.RestoredLatest is { } restored
                ? new { bootImageId = restored.BootImageId, version = restored.Version }
                : null,
        }), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image {BootImageId} (v{Version}) demoted to pre-production; latest restored to v{RestoredVersion}.")]
    private static partial void LogDemoted(ILogger logger, Guid bootImageId, string version, string? restoredVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image {BootImageId} (v{Version}, {Architecture}) promoted to production.")]
    private static partial void LogPromoted(ILogger logger, Guid bootImageId, string version, MachineArchitecture architecture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image deleted: {BootImageId}.")]
    private static partial void LogDeleted(ILogger logger, Guid bootImageId);
}
