using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace CloudImaging.DeviceGatewayApi.Middleware;

/// <summary>
/// Validates the device-session bearer token on all authenticated Device Gateway API endpoints.
/// The public session bootstrap endpoint (POST /api/v1/sessions) is exempt — it has no token yet (FR-014, FR-018).
/// The boot image update check (POST /api/v1/boot-image/latest) is also exempt: the boot image self-update
/// check (T071b, FR-059a) runs independently of session bootstrap/lifecycle, authenticated
/// by the mTLS boot-media certificate (enforced for every request regardless of this middleware)
/// and a signature with the boot media private key.
/// Attaches the validated session ID to the function context for downstream handlers.
/// </summary>
public sealed class DeviceSessionTokenValidationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly Security.DeviceSessionTokenVerifier _verifier;

    /// <param name="verifier">Checks tokens against the hash Imaging Core stored.</param>
    public DeviceSessionTokenValidationMiddleware(Security.DeviceSessionTokenVerifier verifier) => _verifier = verifier;

    /// <summary>Context item key for the resolved session ID.</summary>
    public const string SessionIdKey = "DeviceSessionId";

    /// <summary>Function names exempt from token validation (public/mTLS-only endpoints).</summary>
    /// <remarks>Autopilot availability and submission, and managed Wi-Fi networks, run before, and independently of, any imaging session.</remarks>
    public static readonly IReadOnlySet<string> ExemptFunctionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CreateSession", "CheckLatestBootImage", "GetAutopilotAvailability", "SubmitAutopilotRegistration", "GetManagedWifiNetworks" };

    /// <summary>
    /// Functions authenticated by a per-request Autopilot status token instead of a session token.
    /// The token is only extracted here (so rate limiting can key on it); Imaging Core verifies it.
    /// </summary>
    public static readonly IReadOnlySet<string> StatusTokenFunctionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GetAutopilotRegistrationStatus" };

    /// <summary>Validates the Authorization bearer token and attaches the session ID to <see cref="FunctionContext.Items"/>, or short-circuits with 401.</summary>
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

        if (StatusTokenFunctionNames.Contains(functionName))
        {
            context.Items["BearerToken"] = token;
            await next(context);
            return;
        }

        // Only a token whose hash matches the one Imaging Core stored for its session is accepted.
        var (check, sessionId) = await _verifier.CheckAsync(token, context.CancellationToken);
        if (check == Security.DeviceTokenCheck.Unavailable)
        {
            var unavailable = request.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await unavailable.WriteStringAsync("The session could not be verified. Please retry.");
            context.GetInvocationResult().Value = unavailable;
            return;
        }

        if (check != Security.DeviceTokenCheck.Valid)
        {
            await WriteUnauthorizedAsync(context, request, "Session token rejected or expired.");
            return;
        }

        // A token only ever acts on its own session, whatever the route names.
        if (context.BindingContext.BindingData.TryGetValue("sessionId", out var routeSession) && routeSession is not null
            && (!Guid.TryParse(routeSession.ToString(), out var routeSessionId) || routeSessionId != sessionId))
        {
            context.GetInvocationResult().Value = request.CreateResponse(HttpStatusCode.Forbidden);
            return;
        }

        context.Items[SessionIdKey] = sessionId;
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
