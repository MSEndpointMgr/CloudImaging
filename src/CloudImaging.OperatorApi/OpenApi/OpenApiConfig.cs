using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.OperatorApi.OpenApi;

/// <summary>OpenAPI spec for Operator API (T019).</summary>
public sealed class OpenApiConfig
{
    private const string Spec = """
        {"openapi":"3.1.0","info":{"title":"Cloud Imaging — Operator API","version":"1.0.0"},"paths":{"/api/sessions":{"get":{"operationId":"GetSessions","summary":"List sessions"}},"/api/sessions/couple":{"post":{"operationId":"CoupleSession","summary":"Couple by passcode"}},"/api/sessions/{sessionId}/assign":{"post":{"operationId":"AssignSession","summary":"Assign OS image"}},"/api/sessions/bulk-assign":{"post":{"operationId":"BulkAssign","summary":"Bulk assign"}},"/api/images":{"get":{"operationId":"GetImages"},"post":{"operationId":"CreateImage"}},"/api/boot-images":{"get":{"operationId":"GetBootImages"}},"/api/boot-images/{id}":{"get":{"operationId":"GetBootImageById"}},"/api/boot-images/{id}/sas":{"post":{"operationId":"GetBootImageSasUrl"}},"/api/configuration":{"get":{"operationId":"GetConfiguration"},"put":{"operationId":"PutConfiguration"}},"/api/branding":{"get":{"operationId":"GetBranding"},"put":{"operationId":"PutBranding"}},"/api/cert/generate":{"post":{"operationId":"GenerateCert"}},"/api/cert/rotate":{"post":{"operationId":"RotateCert"}},"/api/bootmedia/certificate/metadata":{"get":{"operationId":"GetCertMetadata"}}}}
        """;

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
