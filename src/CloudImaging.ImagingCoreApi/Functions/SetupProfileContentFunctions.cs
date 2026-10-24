using System.Net;
using System.Text.Json;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Script and answer file texts, called by the Operator API on behalf of the portal.
///
/// POST internal/setup-profiles/content?kind=script|answerFile&amp;uploadedBy=   store one text (raw bytes)
/// GET  internal/setup-profiles/{id}/content/{sha256}            read a text the profile uses
/// </summary>
public sealed class SetupProfileContentFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SetupProfileService _service;

    /// <param name="service">Setup profile rules and storage.</param>
    public SetupProfileContentFunctions(SetupProfileService service) => _service = service;

    /// <summary>POST internal/setup-profiles/content?kind=.</summary>
    [Function("UploadSetupProfileContent")]
    public async Task<HttpResponseData> Upload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/setup-profiles/content")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        long? length = req.Headers.TryGetValues("Content-Length", out var values) && long.TryParse(values.FirstOrDefault(), out var parsed) ? parsed : null;
        var result = await _service.UploadContentAsync(req.Body, query["kind"], length, query["uploadedBy"], ct);
        if (result.Outcome != SetupProfileOutcome.Ok)
        {
            return await SetupProfileFunctions.ProblemAsync(req, result, ct);
        }
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(result.Value, result.Value!.GetType(), JsonOptions), ct);
        return response;
    }

    /// <summary>GET internal/setup-profiles/{id}/content/{sha256}.</summary>
    [Function("GetSetupProfileContent")]
    public async Task<HttpResponseData> Read(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/setup-profiles/{id}/content/{sha256}")] HttpRequestData req,
        string id,
        string sha256,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var text = await _service.GetContentAsync(profileId, sha256, ct);
        if (text is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/plain; charset=utf-8");
        await response.Body.WriteAsync(text.ToMemory(), ct);
        return response;
    }
}
