using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Setup profile endpoints, called by the Operator API on behalf of the portal.
///
/// GET    internal/setup-profiles                          profile list
/// GET    internal/setup-profiles/{id}                     profile with current content
/// POST   internal/setup-profiles                          create (version 1)
/// PUT    internal/setup-profiles/{id}                     save name, targeting and content
/// DELETE internal/setup-profiles/{id}?changedBy=          delete
/// POST   internal/setup-profiles/{id}/restore             restore a version as a new version
/// GET    internal/setup-profiles/{id}/versions            retained versions
/// GET    internal/setup-profiles/{id}/versions/{version}  one version with content
/// GET    internal/setup-profiles/{id}/history             targeting and profile history
/// </summary>
public sealed class SetupProfileFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SetupProfileService _service;

    /// <param name="service">Setup profile rules and storage.</param>
    public SetupProfileFunctions(SetupProfileService service) => _service = service;

    /// <summary>GET internal/setup-profiles.</summary>
    [Function("GetSetupProfiles")]
    public async Task<HttpResponseData> GetSetupProfiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/setup-profiles")] HttpRequestData req,
        FunctionContext context) =>
        await JsonAsync(req, HttpStatusCode.OK, await _service.ListAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>GET internal/setup-profiles/{id}.</summary>
    [Function("GetSetupProfile")]
    public async Task<HttpResponseData> GetSetupProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/setup-profiles/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var detail = await _service.GetDetailAsync(profileId, context.CancellationToken);
        return detail is null
            ? await ProblemAsync(req, SetupProfileResult.NotFound(), context.CancellationToken)
            : await JsonAsync(req, HttpStatusCode.OK, detail, context.CancellationToken);
    }

    /// <summary>POST internal/setup-profiles.</summary>
    [Function("CreateSetupProfile")]
    public async Task<HttpResponseData> CreateSetupProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/setup-profiles")] HttpRequestData req,
        FunctionContext context)
    {
        var request = await ReadAsync<CreateSetupProfileRequest>(req, context.CancellationToken);
        if (request is null)
        {
            return await ProblemAsync(req, SetupProfileResult.BadRequest(InvalidBodyMessage), context.CancellationToken);
        }
        return await ResultAsync(req, await _service.CreateAsync(request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>PUT internal/setup-profiles/{id}.</summary>
    [Function("UpdateSetupProfile")]
    public async Task<HttpResponseData> UpdateSetupProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/setup-profiles/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var request = await ReadAsync<SaveSetupProfileRequest>(req, context.CancellationToken);
        if (request is null)
        {
            return await ProblemAsync(req, SetupProfileResult.BadRequest(InvalidBodyMessage), context.CancellationToken);
        }
        return await ResultAsync(req, await _service.SaveAsync(profileId, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>DELETE internal/setup-profiles/{id}?changedBy=.</summary>
    [Function("DeleteSetupProfile")]
    public async Task<HttpResponseData> DeleteSetupProfile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/setup-profiles/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var changedBy = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["changedBy"];
        return await ResultAsync(req, await _service.DeleteAsync(profileId, changedBy, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/setup-profiles/{id}/restore.</summary>
    [Function("RestoreSetupProfileVersion")]
    public async Task<HttpResponseData> RestoreSetupProfileVersion(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/setup-profiles/{id}/restore")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var request = await ReadAsync<RestoreSetupProfileVersionRequest>(req, context.CancellationToken);
        if (request is null)
        {
            return await ProblemAsync(req, SetupProfileResult.BadRequest(InvalidBodyMessage), context.CancellationToken);
        }
        return await ResultAsync(req, await _service.RestoreAsync(profileId, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET internal/setup-profiles/{id}/versions.</summary>
    [Function("GetSetupProfileVersions")]
    public async Task<HttpResponseData> GetSetupProfileVersions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/setup-profiles/{id}/versions")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var versions = await _service.ListVersionsAsync(profileId, context.CancellationToken);
        return versions is null
            ? await ProblemAsync(req, SetupProfileResult.NotFound(), context.CancellationToken)
            : await JsonAsync(req, HttpStatusCode.OK, versions, context.CancellationToken);
    }

    /// <summary>GET internal/setup-profiles/{id}/versions/{version}.</summary>
    [Function("GetSetupProfileVersion")]
    public async Task<HttpResponseData> GetSetupProfileVersion(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/setup-profiles/{id}/versions/{version}")] HttpRequestData req,
        string id,
        string version,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId) || !int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var detail = await _service.GetVersionAsync(profileId, number, context.CancellationToken);
        return detail is null
            ? await ProblemAsync(req, SetupProfileResult.NotFound(), context.CancellationToken)
            : await JsonAsync(req, HttpStatusCode.OK, detail, context.CancellationToken);
    }

    /// <summary>GET internal/setup-profiles/{id}/history.</summary>
    [Function("GetSetupProfileHistory")]
    public async Task<HttpResponseData> GetSetupProfileHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/setup-profiles/{id}/history")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var profileId))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        var history = await _service.ListHistoryAsync(profileId, context.CancellationToken);
        return history is null
            ? await ProblemAsync(req, SetupProfileResult.NotFound(), context.CancellationToken)
            : await JsonAsync(req, HttpStatusCode.OK, history, context.CancellationToken);
    }

    private const string InvalidBodyMessage = "The request body is not a valid setup profile.";

    private static async Task<HttpResponseData> ResultAsync(HttpRequestData req, SetupProfileResult result, CancellationToken ct) => result.Outcome switch
    {
        SetupProfileOutcome.Ok => await JsonAsync(req, HttpStatusCode.OK, result.Value!, ct),
        SetupProfileOutcome.Created => await JsonAsync(req, HttpStatusCode.Created, result.Value!, ct),
        SetupProfileOutcome.NoContent => req.CreateResponse(HttpStatusCode.NoContent),
        _ => await ProblemAsync(req, result, ct),
    };

    internal static async Task<HttpResponseData> ProblemAsync(HttpRequestData req, SetupProfileResult result, CancellationToken ct)
    {
        var (status, title) = result.Outcome switch
        {
            SetupProfileOutcome.NotFound => (HttpStatusCode.NotFound, "Not found."),
            SetupProfileOutcome.Conflict => (HttpStatusCode.Conflict, "Conflict."),
            _ => (HttpStatusCode.BadRequest, "Invalid request."),
        };
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        var body = new Dictionary<string, object?>
        {
            ["type"] = "https://cloudimaging.io/errors/setup-profile",
            ["title"] = title,
            ["status"] = (int)status,
            ["detail"] = result.Detail,
        };
        if (result.Errors is { Count: > 0 } errors)
        {
            body["errors"] = errors.Select(e => new { code = e.Code, line = e.Line, message = e.Message });
        }
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions), ct);
        return response;
    }

    private static async Task<T?> ReadAsync<T>(HttpRequestData req, CancellationToken ct) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions, ct);
        }
        catch (JsonException) { return null; }
    }

    private static async Task<HttpResponseData> JsonAsync(HttpRequestData req, HttpStatusCode status, object body, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, body.GetType(), JsonOptions), ct);
        return response;
    }
}
