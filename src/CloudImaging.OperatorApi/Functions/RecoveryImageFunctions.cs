using System.Net;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Proxy endpoints for the recovery (WinRE) image catalog, mirroring the boot image proxy
/// functions. Forwards requests to the internal Imaging Core API after role enforcement has
/// already been applied by middleware.
///
/// GET    /api/recovery-images              — list active recovery images
/// POST   /api/recovery-images/{id}/sas     — issue a SAS URL for a recovery image
/// DELETE /api/recovery-images/{id}         — delete a recovery image
/// POST   /api/recovery-images/upload/start — start a staged upload
/// POST   /api/recovery-images/upload/{uploadId}/publish — commit + publish a staged upload
/// </summary>
public sealed partial class RecoveryImageFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<RecoveryImageFunctions> _logger;

    public RecoveryImageFunctions(ImagingCoreClient coreClient, ILogger<RecoveryImageFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    [Function(nameof(GetRecoveryImages))]
    public async Task<HttpResponseData> GetRecoveryImages(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "recovery-images")] HttpRequestData req,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GetRecoveryImagesAsync(context.CancellationToken);
        return await ForwardAsync(req, coreResponse, context.CancellationToken);
    }

    [Function(nameof(GetRecoveryImageSas))]
    public async Task<HttpResponseData> GetRecoveryImageSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recovery-images/{id}/sas")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var recoveryImageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var coreResponse = await _coreClient.GetRecoveryImageSasAsync(recoveryImageId, context.CancellationToken);
        return await ForwardAsync(req, coreResponse, context.CancellationToken);
    }

    [Function(nameof(DeleteRecoveryImage))]
    public async Task<HttpResponseData> DeleteRecoveryImage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "recovery-images/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var recoveryImageId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var coreResponse = await _coreClient.DeleteRecoveryImageAsync(recoveryImageId, context.CancellationToken);
        return await ForwardAsync(req, coreResponse, context.CancellationToken);
    }

    [Function(nameof(StartRecoveryImageUpload))]
    public async Task<HttpResponseData> StartRecoveryImageUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recovery-images/upload/start")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var coreResponse = await _coreClient.StartRecoveryImageUploadAsync(body.RootElement, context.CancellationToken);
        return await ForwardAsync(req, coreResponse, context.CancellationToken);
    }

    [Function(nameof(PublishRecoveryImageUpload))]
    public async Task<HttpResponseData> PublishRecoveryImageUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "recovery-images/upload/{uploadId}/publish")] HttpRequestData req,
        string uploadId,
        FunctionContext context)
    {
        using var body = await System.Text.Json.JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var coreResponse = await _coreClient.PublishRecoveryImageUploadAsync(uploadId, body.RootElement, context.CancellationToken);
        return await ForwardAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<HttpResponseData> ForwardAsync(
        HttpRequestData req, HttpResponseMessage coreResponse, CancellationToken ct)
    {
        var response = req.CreateResponse(coreResponse.StatusCode);
        var content = await coreResponse.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrEmpty(content))
        {
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(content, ct);
        }
        return response;
    }
}
