using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.DeviceGatewayApi.OpenApi;

/// <summary>OpenAPI spec for Device Gateway API (T019).</summary>
public sealed class OpenApiConfig
{
    private const string Spec = """
        {"openapi":"3.1.0","info":{"title":"Cloud Imaging: Device Gateway API","version":"1.0.0"},"paths":{"/api/v1/sessions":{"post":{"operationId":"CreateSession","summary":"Register a new imaging session","responses":{"201":{"description":"Session created"},"400":{"description":"Invalid payload"}}}},"/api/v1/sessions/{sessionId}/status":{"get":{"operationId":"GetSessionStatus","summary":"Poll session state","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"Status with currentStep and overallProgressPercent"}}}},"/api/v1/sessions/{sessionId}/progress":{"post":{"operationId":"ReportProgress","summary":"Submit step progress; a final report may carry warningCount and items","security":[{"BearerAuth":[]}],"responses":{"204":{"description":"Recorded"}}}},"/api/v1/sessions/{sessionId}/setup-profile":{"get":{"operationId":"GetSessionSetupProfile","summary":"Pinned setup profile version with short-lived read URLs for the document, the scripts this device runs and the answer file","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"Descriptor"},"204":{"description":"Nothing pinned"},"403":{"description":"Session does not match the token"},"409":{"description":"Session is not imaging"},"503":{"description":"Imaging Core unavailable"}}}},"/api/v1/sessions/{sessionId}/driver-set":{"get":{"operationId":"GetSessionDriverSet","summary":"Driver set pinned at assignment with a 60-minute read link per component, or the reason no set applies","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"Descriptor"},"403":{"description":"Session does not match the token"},"404":{"description":"No driver work for this session"},"409":{"description":"Session is not imaging"},"429":{"description":"Rate limited"},"503":{"description":"Imaging Core unavailable"}}}},"/api/v1/sessions/{sessionId}/driver-set/components/{index}/link":{"post":{"operationId":"RefreshSessionDriverComponentLink","summary":"New read link for one component of the pinned driver set","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"downloadUrl and downloadUrlExpiresAt"},"400":{"description":"Index outside the set"},"403":{"description":"Session does not match the token"},"409":{"description":"Session is not imaging"},"503":{"description":"Imaging Core unavailable"}}}},"/api/v1/sessions/{sessionId}/sas/refresh":{"post":{"operationId":"RefreshSasToken","summary":"Refresh SAS URL","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"New SAS URL"}}}},"/api/v1/sessions/{sessionId}/cache/validate":{"post":{"operationId":"ValidateCacheHash","summary":"Validate cached WIM hash","security":[{"BearerAuth":[]}],"responses":{"200":{"description":"{ valid: bool }"}}}}},"components":{"securitySchemes":{"BearerAuth":{"type":"http","scheme":"bearer"}}}}
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
