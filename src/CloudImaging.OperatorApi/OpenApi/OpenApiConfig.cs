using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.OperatorApi.OpenApi;

/// <summary>OpenAPI spec for Operator API (T019).</summary>
public sealed class OpenApiConfig
{
    private const string Spec = """
        {"openapi":"3.1.0","info":{"title":"Cloud Imaging: Operator API","version":"1.0.0"},"paths":{"/api/sessions":{"get":{"operationId":"GetSessions","summary":"List sessions"}},"/api/sessions/couple":{"post":{"operationId":"CoupleSession","summary":"Couple by passcode"}},"/api/sessions/{sessionId}/assign":{"post":{"operationId":"AssignSession","summary":"Assign OS image"}},"/api/sessions/bulk-assign":{"post":{"operationId":"BulkAssign","summary":"Bulk assign"}},"/api/session-history":{"get":{"operationId":"GetSessionHistory","summary":"List session history"}},"/api/images":{"get":{"operationId":"GetImages"},"post":{"operationId":"CreateImage"}},"/api/boot-images":{"get":{"operationId":"GetBootImages"}},"/api/boot-images/{id}":{"get":{"operationId":"GetBootImageById"}},"/api/boot-images/{id}/sas":{"post":{"operationId":"GetBootImageSasUrl"}},"/api/configuration":{"get":{"operationId":"GetConfiguration"},"put":{"operationId":"PutConfiguration"}},"/api/branding":{"get":{"operationId":"GetBranding"},"put":{"operationId":"PutBranding"}},"/api/cert/generate":{"post":{"operationId":"GenerateCert"}},"/api/cert/rotate":{"post":{"operationId":"RotateCert"}},"/api/bootmedia/certificate/metadata":{"get":{"operationId":"GetCertMetadata"}},"/api/sessions/{sessionId}/steps/{stepName}/items":{"get":{"operationId":"GetSessionStepItems","summary":"Per-item results a device reported for one step"}},"/api/setup-profiles":{"get":{"operationId":"GetSetupProfiles","summary":"List setup profiles"},"post":{"operationId":"CreateSetupProfile","summary":"Create a setup profile"}},"/api/setup-profiles/{id}":{"get":{"operationId":"GetSetupProfile","summary":"Setup profile with current content"},"put":{"operationId":"UpdateSetupProfile","summary":"Save name, targeting and content"},"delete":{"operationId":"DeleteSetupProfile","summary":"Delete a setup profile"}},"/api/setup-profiles/{id}/restore":{"post":{"operationId":"RestoreSetupProfileVersion","summary":"Restore a version as a new version"}},"/api/setup-profiles/{id}/versions":{"get":{"operationId":"GetSetupProfileVersions","summary":"Retained versions"}},"/api/setup-profiles/{id}/versions/{version}":{"get":{"operationId":"GetSetupProfileVersion","summary":"One version with content"}},"/api/setup-profiles/{id}/history":{"get":{"operationId":"GetSetupProfileHistory","summary":"Targeting and profile history"}}}}
        """;

    /// <summary>GET openapi. Serves the static OpenAPI 3.1 spec document for this API.</summary>
    [Function("GetOpenApiSpec")]
    public static async Task<HttpResponseData> GetOpenApiSpec(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "openapi")] HttpRequestData req,
        FunctionContext context)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(Spec, context.CancellationToken);
        return response;
    }
}
