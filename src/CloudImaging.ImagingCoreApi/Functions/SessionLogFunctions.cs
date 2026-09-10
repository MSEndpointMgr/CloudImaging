using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Client-uploaded diagnostic log storage. The Cloud Imaging Client uploads its current local
/// rolling log file to blob storage on any terminal imaging failure (best-effort, never blocks
/// the failure result), so support can inspect the full log without depending on the technician
/// retrieving it from a WinPE session before reboot. Portal operators list/download uploaded
/// logs for a session via the Operator API proxy (<c>SessionQueryFunctions</c>).
///
/// POST /api/internal/sessions/{sessionId}/logs/upload-url             — issue a write SAS URL
/// GET  /api/internal/sessions/{sessionId}/logs                        — list uploaded logs
/// GET  /api/internal/sessions/{sessionId}/logs/{fileName}/download-url — issue a read SAS URL
///
/// Blobs are stored at <c>session-logs/{sessionId}/{fileName}</c>; the container has a 90-day
/// lifecycle expiry policy (see modules/storage.bicep) so no explicit cleanup is needed here.
/// </summary>
public sealed partial class SessionLogFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string Container = "session-logs";

    private readonly BlobServiceClient _blobClient;
    private readonly ILogger<SessionLogFunctions> _logger;

    public SessionLogFunctions(BlobServiceClient blobClient, ILogger<SessionLogFunctions> logger)
    {
        _blobClient = blobClient;
        _logger = logger;
    }

    [Function(nameof(RequestLogUploadUrl))]
    public async Task<HttpResponseData> RequestLogUploadUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/{sessionId}/logs/upload-url")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var fileName = $"client-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.log";
        var blobName = $"{sessionGuid}/{fileName}";

        // Staging a blob into a container that does not exist fails the device's PUT with a bare
        // 404, and the container is only declared in the deployment template, so any environment
        // provisioned before it was added would silently lose every diagnostic log. Creating it
        // here costs one call on a path only reached when imaging has already failed.
        await _blobClient.GetBlobContainerClient(Container)
            .CreateIfNotExistsAsync(cancellationToken: context.CancellationToken);

        var uploadUrl = await BlobSasUrlGenerator.GenerateAsync(
            _blobClient,
            Container,
            blobName,
            BlobSasPermissions.Create | BlobSasPermissions.Write,
            TimeSpan.FromMinutes(15),
            context.CancellationToken);

        LogUploadUrlIssued(_logger, sessionGuid, fileName);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            fileName,
            uploadUrl,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        }, JsonOptions), context.CancellationToken);
        return response;
    }

    [Function(nameof(ListSessionLogs))]
    public async Task<HttpResponseData> ListSessionLogs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions/{sessionId}/logs")] HttpRequestData req,
        string sessionId,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var container = _blobClient.GetBlobContainerClient(Container);
        var prefix = $"{sessionGuid}/";
        var items = new List<object>();

        try
        {
            await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: context.CancellationToken))
            {
                items.Add(new
                {
                    fileName = blob.Name[prefix.Length..],
                    sizeBytes = blob.Properties.ContentLength ?? 0,
                    uploadedAt = blob.Properties.LastModified,
                });
            }
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            // The container is created lazily on the first log upload, so "no container" and
            // "no logs for this session" are the same answer to the portal: an empty list, not a
            // 500 that surfaces as "Could not retrieve logs for this session".
            LogContainerMissing(_logger, sessionGuid);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(items, JsonOptions), context.CancellationToken);
        return response;
    }

    [Function(nameof(GetLogDownloadUrl))]
    public async Task<HttpResponseData> GetLogDownloadUrl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions/{sessionId}/logs/{fileName}/download-url")] HttpRequestData req,
        string sessionId,
        string fileName,
        FunctionContext context)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var blobName = $"{sessionGuid}/{fileName}";
        var blobClient = _blobClient.GetBlobContainerClient(Container).GetBlobClient(blobName);
        if (!await blobClient.ExistsAsync(context.CancellationToken))
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var downloadUrl = await BlobSasUrlGenerator.GenerateAsync(
            _blobClient,
            Container,
            blobName,
            BlobSasPermissions.Read,
            TimeSpan.FromMinutes(15),
            // The Portal opens this URL directly in a browser tab. Client logs are UTF-8, so the
            // charset is pinned here rather than left to the browser's locale default — which
            // renders "→" as "â†'". Applied at download time so logs uploaded before the client
            // started sending a charset are readable too.
            responseContentType: "text/plain; charset=utf-8",
            cancellationToken: context.CancellationToken);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            downloadUrl,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        }, JsonOptions), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session log upload URL issued for session {SessionId}: {FileName}.")]
    private static partial void LogUploadUrlIssued(ILogger logger, Guid sessionId, string fileName);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No session-logs container exists yet; returning no logs for session {SessionId}.")]
    private static partial void LogContainerMissing(ILogger logger, Guid sessionId);
}
