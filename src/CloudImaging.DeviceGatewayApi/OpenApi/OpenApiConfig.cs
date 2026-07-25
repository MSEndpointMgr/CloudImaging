using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.DeviceGatewayApi.OpenApi;

/// <summary>OpenAPI spec for Device Gateway API (T019).</summary>
public sealed class OpenApiConfig
{
    private const string Spec = """
        {"openapi":"3.1.0","info":{"title":"Cloud Imaging — Device Gateway API","version":"1.0.0"},"paths":{"/api/v1/sessions":{"post":{"operationId":"CreateSession","summary":"Register a new imaging session","responses":{"201":{"description":"Session created"},"400":{"description":"Invalid payload"}}}},"/api/v1/sessions/{sessionId}/status":{"get":{"operationId":"GetSessionStatus","summary":"Poll session state","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"Status with currentStep and overallProgressPercent"}}}},"/api/v1/sessions/{sessionId}/progress":{"post":{"operationId":"ReportProgress","summary":"Submit step progress","security":[{"BearerAuth":[]}],"responses":{"204":{"description":"Recorded"}}}},"/api/v1/sessions/{sessionId}/sas/refresh":{"post":{"operationId":"RefreshSasToken","summary":"Refresh SAS URL","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"New SAS URL"}}}},"/api/v1/sessions/{sessionId}/cache/validate":{"post":{"operationId":"ValidateCacheHash","summary":"Validate cached WIM hash","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"{ valid: bool }"}}}}},"components":{"securitySchemes":{"BearerAuth":{"type":"http","scheme":"bearer"}}}}
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
