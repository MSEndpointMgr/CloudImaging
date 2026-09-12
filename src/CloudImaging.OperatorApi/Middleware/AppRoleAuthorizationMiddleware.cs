using System.Net;
using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;

namespace CloudImaging.OperatorApi.Middleware;

/// <summary>
/// Enforces service-level app role authorization on Operator API endpoints.
/// Accepted roles: CloudImaging.PortalAccess (full access) and CloudImaging.MediaBuilderAccess (read-only access to boot images and cert metadata).
/// Roles are carried in the JWT 'roles' claim for service principals (FR-061, FR-062).
/// </summary>
public sealed class AppRoleAuthorizationMiddleware : IFunctionsWorkerMiddleware
{
    /// <summary>Function context item key under which the resolved app role is stored.</summary>
    public const string ResolvedRoleKey = "OperatorApiRole";

    /// <summary>The app role granting full access to the Operator API.</summary>
    public const string PortalAccessRole = "CloudImaging.PortalAccess";

    /// <summary>The app role granting read-only access to boot media assets.</summary>
    public const string MediaBuilderAccessRole = "CloudImaging.MediaBuilderAccess";

    // Endpoints accessible by MediaBuilderAccess (read-only subset) — all others require PortalAccess
    private static readonly HashSet<string> MediaBuilderAllowedFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "GetBootImages",
            "GetBootImageSasUrl",
            "GetBrandingLogoSas",
            "GetBootMediaCertificateMetadata",
            "GetBootMediaCertificatePfx",
            "GetEndpointConfiguration",
            "GetLocations",
        };

    /// <summary>Enforces app role authorization and stores the resolved role in the function context.</summary>
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (!context.Items.TryGetValue(EntraAuthMiddleware.ClaimsPrincipalKey, out var principalObj)
            || principalObj is not ClaimsPrincipal principal)
        {
            // EntraAuthMiddleware already rejected the request; this shouldn't be reached
            await next(context);
            return;
        }

        // Extract roles from the validated 'roles' claim
        var roles = principal
            .FindAll("roles")
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var functionName = context.FunctionDefinition.Name;
        bool hasPortalAccess = roles.Contains(PortalAccessRole);
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
        if (request is null)
        {
            return;
        }

        var response = request.CreateResponse(HttpStatusCode.Forbidden);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(
            $$"""{"type":"https://cloudimaging.io/errors/forbidden","title":"Forbidden","status":403,"detail":"{{detail}}"}""");
        context.GetInvocationResult().Value = response;
    }
}
