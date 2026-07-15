using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using System.IdentityModel.Tokens.Jwt;
using System.Net;

namespace CloudImaging.OperatorApi.Middleware;

/// <summary>
/// Enforces service-level app role authorization on Operator API endpoints.
/// Accepted roles: CloudImaging.PortalAccess (full access) and CloudImaging.MediaBuilderAccess (read-only access to boot images and cert metadata).
/// Roles are carried in the JWT 'roles' claim for service principals (FR-061, FR-062).
/// </summary>
public sealed class AppRoleAuthorizationMiddleware : IFunctionsWorkerMiddleware
{
    public const string ResolvedRoleKey = "OperatorApiRole";

    public const string PortalAccessRole        = "CloudImaging.PortalAccess";
    public const string MediaBuilderAccessRole  = "CloudImaging.MediaBuilderAccess";

    // Endpoints accessible by MediaBuilderAccess (read-only subset) — all others require PortalAccess
    private static readonly HashSet<string> MediaBuilderAllowedFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "GetBootImages",
            "GetBootImageSas",
            "GetBrandingLogoSas",
            "GetBootMediaCertificateMetadata",
            "GetBootMediaCertificatePfx",
        };

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (!context.Items.TryGetValue(EntraAuthMiddleware.ClaimsPrincipalKey, out var tokenObj)
            || tokenObj is not JwtSecurityToken jwt)
        {
            // EntraAuthMiddleware already rejected the request; this shouldn't be reached
            await next(context);
            return;
        }

        // Extract roles from the JWT 'roles' claim
        var roles = jwt.Claims
            .Where(c => c.Type == "roles")
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var functionName = context.FunctionDefinition.Name;
        bool hasPortalAccess       = roles.Contains(PortalAccessRole);
        bool hasMediaBuilderAccess = roles.Contains(MediaBuilderAccessRole);

        if (!hasPortalAccess && !hasMediaBuilderAccess)
        {
            await WriteForbiddenAsync(context, "Caller does not hold any recognised Operator API app role.");
            return;
        }

        if (!hasPortalAccess && !MediaBuilderAllowedFunctions.Contains(functionName))
        {
            await WriteForbiddenAsync(context,
                $"CloudImaging.MediaBuilderAccess does not permit access to '{functionName}'. CloudImaging.PortalAccess is required.");
            return;
        }

        context.Items[ResolvedRoleKey] = hasPortalAccess ? PortalAccessRole : MediaBuilderAccessRole;
        await next(context);
    }

    private static async Task WriteForbiddenAsync(FunctionContext context, string detail)
    {
        var request = await context.GetHttpRequestDataAsync();
        if (request is null) return;

        var response = request.CreateResponse(HttpStatusCode.Forbidden);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(
            $$"""{"type":"https://cloudimaging.io/errors/forbidden","title":"Forbidden","status":403,"detail":"{{detail}}"}""");
        context.GetInvocationResult().Value = response;
    }
}
