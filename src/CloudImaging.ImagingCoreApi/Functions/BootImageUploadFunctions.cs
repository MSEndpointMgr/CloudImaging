using System.IO;
using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Staged boot image upload and publish-commit endpoints (T125, T125a, FR-063).
///
/// POST   /api/internal/boot-images/upload/start      — start a staged upload session; returns blobUploadUrl
/// POST   /api/internal/boot-images/upload/{token}/publish — validate SHA-256 and publish
/// </summary>
public sealed partial class BootImageUploadFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string UploadContainer = "boot-images";

    private readonly UploadJobRepository _jobRepo;
    private readonly BootImageValidationService _validator;
    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<BootImageUploadFunctions> _logger;

    public BootImageUploadFunctions(
        UploadJobRepository jobRepo,
        BootImageValidationService validator,
        BlobServiceClient blobClient,
        ILogger<BootImageUploadFunctions> logger)
    {
        _jobRepo = jobRepo;
        _validator = validator;
        _blobClient = blobClient;
        _logger = logger;
    }

    // ── POST /api/internal/boot-images/upload/start ───────────────────────────

    [Function("StartBootImageUpload")]
    public async Task<HttpResponseData> StartUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/boot-images/upload/start")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("version", out var versionProp)
            || !body.RootElement.TryGetProperty("sha256Hash", out var hashProp)
            || !body.RootElement.TryGetProperty("fileName", out var fileNameProp))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var version = versionProp.GetString() ?? string.Empty;
        var sha256Hash = hashProp.GetString() ?? string.Empty;
        var fileName = fileNameProp.GetString() ?? string.Empty;
        var extension = Path.GetExtension(fileName);

        if (!BootImageValidationService.IsAllowedExtension(extension, BootImageValidationService.WimOnlyExtensions))
        {
            LogUnsupportedExtension(_logger, extension);
            var rejected = req.CreateResponse(HttpStatusCode.BadRequest);
            await rejected.WriteStringAsync(
                $"Unsupported file extension '{extension}'. Only {string.Join(", ", BootImageValidationService.WimOnlyExtensions)} are allowed.",
                context.CancellationToken);
            return rejected;
        }

        var uploadId = Guid.NewGuid().ToString("N");
        var blobName = $"uploads/{uploadId}/{version.Replace(' ', '-')}{extension}";

        // Generate a SAS URL with Write permission for the client to upload directly to Blob Storage
        var uploadUrl = await BlobSasUrlGenerator.GenerateAsync(
            _blobClient,
            UploadContainer,
            blobName,
            Azure.Storage.Sas.BlobSasPermissions.Create | Azure.Storage.Sas.BlobSasPermissions.Write,
            TimeSpan.FromHours(4),
            context.CancellationToken);

        LogUploadStarted(_logger, uploadId, version);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            uploadId,
            blobName,
            uploadUrl,
            sha256Hash,
            expiresAt = DateTimeOffset.UtcNow.AddHours(4),
        }, JsonOptions), context.CancellationToken);
        return response;
    }

    // ── POST /api/internal/boot-images/upload/{token}/publish ─────────────────

    [Function("PublishBootImageUpload")]
    public async Task<HttpResponseData> PublishUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/boot-images/upload/{token}/publish")] HttpRequestData req,
        string token,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        if (!body.RootElement.TryGetProperty("blobName", out var blobNameProp)
            || !body.RootElement.TryGetProperty("sha256Hash", out var hashProp)
            || !body.RootElement.TryGetProperty("version", out var versionProp)
            || !body.RootElement.TryGetProperty("sizeBytes", out var sizeProp))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var blobName = blobNameProp.GetString()!;
        var sha256Hash = hashProp.GetString()!;
        var version = versionProp.GetString()!;
        var sizeBytes = sizeProp.GetInt64();
        var extension = Path.GetExtension(blobName);

        // Only the cheap file-signature check (a ranged read of the first ~36 KB) runs inline, so a
        // wrong file is still rejected immediately. The full-file SHA-256 and the copy to the
        // published prefix scale with image size and would blow the 45s Static Web Apps request
        // cap, so they are handed to the background worker (see UploadJob).
        var blobClient = _blobClient.GetBlobContainerClient(UploadContainer).GetBlobClient(blobName);
        var signature = await _validator.ValidateSignatureAsync(
            blobClient, extension, BootImageValidationService.WimOnlyExtensions, context.CancellationToken);

        if (!signature.Valid)
        {
            LogValidationFailed(_logger, blobName, signature.FailureReason ?? "unknown");
            await blobClient.DeleteIfExistsAsync(cancellationToken: context.CancellationToken);
            var bad = req.CreateResponse(HttpStatusCode.UnprocessableEntity);
            await bad.WriteStringAsync(signature.FailureReason ?? "File signature validation failed.", context.CancellationToken);
            return bad;
        }

        var now = DateTimeOffset.UtcNow;
        var job = new UploadJob
        {
            UploadId = token,
            Kind = UploadJobKind.BootImage,
            Status = UploadJobStatus.Pending,
            BlobName = blobName,
            Sha256Hash = sha256Hash,
            Version = version,
            SizeBytes = sizeBytes,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var accepted = await _jobRepo.CreateOrGetAsync(job, context.CancellationToken);
        LogPublishAccepted(_logger, token, version);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(accepted, JsonOptions), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image upload started: id={UploadId} version={Version}.")]
    private static partial void LogUploadStarted(ILogger logger, string uploadId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image validation failed for {BlobName}: {Reason}.")]
    private static partial void LogValidationFailed(ILogger logger, string blobName, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Boot image publish accepted for background processing: id={UploadId} v{Version}.")]
    private static partial void LogPublishAccepted(ILogger logger, string uploadId, string version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Boot image upload rejected: unsupported extension '{Extension}'.")]
    private static partial void LogUnsupportedExtension(ILogger logger, string extension);
}
