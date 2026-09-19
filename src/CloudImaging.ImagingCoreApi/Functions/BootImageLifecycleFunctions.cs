using System.Net;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Boot image lifecycle CRUD endpoints (T115, FR-063).
///
/// DELETE /api/internal/boot-images/{id} — soft-delete (deactivate) a boot image
///
/// Publishing lives in BootImageUploadFunctions, which only writes a catalog entry for a blob
/// it staged and verified itself. Accepting a caller-supplied storage path here would let the
/// catalog point at unverified content.
/// </summary>
public sealed partial class BootImageLifecycleFunctions
{
    private readonly BootImageRepository _repo;
    private readonly ILogger<BootImageLifecycleFunctions> _logger;

    /// <summary>Initializes a new instance of <see cref="BootImageLifecycleFunctions"/>.</summary>
    public BootImageLifecycleFunctions(
        BootImageRepository repo,
        ILogger<BootImageLifecycleFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    // ── DELETE /api/internal/boot-images/{id} ────────────────────────────────

    /// <summary>Soft-deletes (deactivates) a boot image by id.</summary>
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image deleted: {BootImageId}.")]
    private static partial void LogDeleted(ILogger logger, Guid bootImageId);
}
