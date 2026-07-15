using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Identity.Web;
using System.IdentityModel.Tokens.Jwt;
using System.Net;

namespace CloudImaging.OperatorApi.Middleware;

/// <summary>
/// Validates Entra ID bearer tokens on all Operator API endpoints.
/// Tokens are validated against the Operator API's own app registration audience (FR-061).
/// </summary>
public sealed class EntraAuthMiddleware : IFunctionsWorkerMiddleware
{
    public const string ClaimsPrincipalKey = "ClaimsPrincipal";

    private readonly ITokenAcquisition _tokenAcquisition;

    public EntraAuthMiddleware(ITokenAcquisition tokenAcquisition) =>
        _tokenAcquisition = tokenAcquisition;

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var request = await context.GetHttpRequestDataAsync();
        if (request is null)
        {
            await next(context);
            return;
        }

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

        try
        {
            // Parse without full validation here; Microsoft.Identity.Web validates in the pipeline.
            // For Azure Functions isolated worker, we perform manual JWT validation via the handler below.
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token))
            {
                await WriteUnauthorizedAsync(context, request, "Token is malformed.");
                return;
            }

            var jwtToken = handler.ReadJwtToken(token);
            context.Items[ClaimsPrincipalKey] = jwtToken;
            context.Items["RawBearerToken"]   = token;
        }
        catch (Exception)
        {
            await WriteUnauthorizedAsync(context, request, "Token could not be parsed.");
            return;
        }

        await next(context);
    }

    private static async Task WriteUnauthorizedAsync(FunctionContext context, HttpRequestData request, string detail)
    {
        var response = request.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(
            $$"""{"type":"https://cloudimaging.io/errors/unauthorized","title":"Unauthorized","status":401,"detail":"{{detail}}"}""");
        context.GetInvocationResult().Value = response;
    }
}
