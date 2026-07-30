using System.Net;
using CloudImaging.OperatorApi.Security;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.IdentityModel.Tokens;

namespace CloudImaging.OperatorApi.Middleware;

/// <summary>
/// Validates Entra ID bearer tokens on all Operator API endpoints.
/// Tokens are validated against the Operator API's own app registration audience (FR-061):
/// cryptographic signature, issuer, audience and lifetime are all verified via
/// <see cref="EntraTokenValidator"/>.
/// </summary>
public sealed class EntraAuthMiddleware : IFunctionsWorkerMiddleware
{
    public const string ClaimsPrincipalKey = "ClaimsPrincipal";

    private readonly EntraTokenValidator _tokenValidator;

    public EntraAuthMiddleware(EntraTokenValidator tokenValidator) => _tokenValidator = tokenValidator;

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
            // Full validation: signature (tenant JWKS), issuer, audience and lifetime.
            var principal = await _tokenValidator.ValidateAsync(token, context.CancellationToken);
            context.Items[ClaimsPrincipalKey] = principal;
            context.Items["RawBearerToken"] = token;
        }
        catch (SecurityTokenException)
        {
            await WriteUnauthorizedAsync(context, request, "Token validation failed.");
            return;
        }
        catch (Exception)
        {
            await WriteUnauthorizedAsync(context, request, "Token could not be validated.");
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
