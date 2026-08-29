using System.Net;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Upload job status proxy.
/// GET /api/upload-jobs/{uploadId} → ImagingCore upload job status
///
/// The three publish endpoints (OS, boot and recovery images) return 202 Accepted because the
/// full-file SHA-256 verification, ISO extraction and blob move all scale with image size and
/// cannot fit inside the 45 second Azure Static Web Apps request cap. The portal polls this
/// endpoint until the job reaches Completed or Failed.
///
/// Requires CloudImaging.PortalAccess role (enforced by AppRoleAuthorizationMiddleware).
/// </summary>
public sealed class UploadJobFunctions
{
    private readonly ImagingCoreClient _coreClient;

    public UploadJobFunctions(ImagingCoreClient coreClient)
    {
        _coreClient = coreClient;
    }

    [Function("GetUploadJob")]
    public async Task<HttpResponseData> GetUploadJob(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "upload-jobs/{uploadId}")] HttpRequestData req,
        string uploadId,
        FunctionContext context)
    {
        var core = await _coreClient.GetUploadJobAsync(uploadId, context.CancellationToken);

        var response = req.CreateResponse((HttpStatusCode)(int)core.StatusCode);
        var body = await core.Content.ReadAsStringAsync(context.CancellationToken);
        if (!string.IsNullOrEmpty(body))
        {
            response.Headers.Add("Content-Type", core.Content.Headers.ContentType?.MediaType ?? "text/plain");
            await response.WriteStringAsync(body, context.CancellationToken);
        }
        return response;
    }
}
