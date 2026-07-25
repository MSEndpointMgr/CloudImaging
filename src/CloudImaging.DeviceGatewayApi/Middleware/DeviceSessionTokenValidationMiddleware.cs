using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace CloudImaging.DeviceGatewayApi.Middleware;

/// <summary>
/// Validates the device-session bearer token on all authenticated Device Gateway API endpoints.
/// The public session bootstrap endpoint (POST /api/v1/sessions) is exempt — it has no token yet (FR-014, FR-018).
/// Attaches the validated session ID to the function context for downstream handlers.
/// </summary>
public sealed class DeviceSessionTokenValidationMiddleware : IFunctionsWorkerMiddleware
{
    /// <summary>Context item key for the resolved session ID.</summary>
    public const string SessionIdKey = "DeviceSessionId";

    /// <summary>Function names exempt from token validation (the public bootstrap endpoint).</summary>
    public static readonly IReadOnlySet<string> ExemptFunctionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CreateSession" };

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var functionName = context.FunctionDefinition.Name;

        if (ExemptFunctionNames.Contains(functionName))
        {
            await next(context);
            return;
        }

        var request = await context.GetHttpRequestDataAsync();
        if (request is null)
        {
            await next(context);
            return;
        }

        // Extract Bearer token from Authorization header
        if (!request.Headers.TryGetValues("Authorization", out var authValues))
        {
            await WriteUnauthorizedAsync(context, request, "Missing Authorization header.");
            return;
        }

        var authHeader = authValues.FirstOrDefault() ?? string.Empty;
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            await WriteUnauthorizedAsync(context, request, "Authorization header must use Bearer scheme.");
            return;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            await WriteUnauthorizedAsync(context, request, "Bearer token is empty.");
            return;
        }

        // The actual hash verification against the stored session token happens in the
        // ImagingCoreClient call — here we just extract and route the session context.
        var sessionId = Security.DeviceSessionTokenService.ExtractSessionId(token);
        if (sessionId is null)
        {
            await WriteUnauthorizedAsync(context, request, "Bearer token is malformed.");
            return;
        }

        // Store raw token and session hint for downstream handlers to validate against ImagingCoreApi
        context.Items[SessionIdKey] = sessionId.Value;
        context.Items["BearerToken"] = token;

        await next(context);
    }

    private static async Task WriteUnauthorizedAsync(
        FunctionContext context,
        HttpRequestData request,
        string detail)
    {
        var response = request.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(
            $$"""{"type":"https://cloudimaging.io/errors/unauthorized","title":"Unauthorized","status":401,"detail":"{{detail}}"}""");
        context.GetInvocationResult().Value = response;
    }
}
