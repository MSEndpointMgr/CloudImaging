using System.Net;
using System.Text.Json;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Per-portal-user location preference proxy endpoints (Location Labels feature). userId is the
/// caller's Entra object id (oid), extracted by the portal server from the validated user JWT
/// and forwarded here as a plain route parameter — PortalAccess (service-to-service) only;
/// Media Builder has no concept of "the signed-in portal user" and never calls this.
///
/// GET /api/user-preferences/{userId} — returns the user's preferred location (or 404)
/// PUT /api/user-preferences/{userId} — sets/clears the user's preferred location
/// </summary>
public sealed class UserPreferencesFunctions
{
    private readonly ImagingCoreClient _coreClient;

    public UserPreferencesFunctions(ImagingCoreClient coreClient) => _coreClient = coreClient;

    [Function("GetUserLocationPreference")]
    public async Task<HttpResponseData> GetUserLocationPreference(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "user-preferences/{userId}")] HttpRequestData req,
        string userId,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GetUserLocationPreferenceAsync(userId, context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    [Function("PutUserLocationPreference")]
    public async Task<HttpResponseData> PutUserLocationPreference(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "user-preferences/{userId}")] HttpRequestData req,
        string userId,
        FunctionContext context)
    {
        using var doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
        var payload = JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText());
        var coreResponse = await _coreClient.PutUserLocationPreferenceAsync(userId, payload!, context.CancellationToken);
        return await ProxyResponseAsync(req, coreResponse, context.CancellationToken);
    }

    private static async Task<HttpResponseData> ProxyResponseAsync(
        HttpRequestData req,
        HttpResponseMessage coreResponse,
        CancellationToken ct)
    {
        var response = req.CreateResponse((HttpStatusCode)((int)coreResponse.StatusCode));
        if (coreResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            response.Headers.Add("Content-Type", "application/json");
            var content = await coreResponse.Content.ReadAsStringAsync(ct);
            await response.WriteStringAsync(content, ct);
        }
        return response;
    }
}
