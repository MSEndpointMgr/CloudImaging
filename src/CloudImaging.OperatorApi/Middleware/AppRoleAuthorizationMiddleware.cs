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
    /// <summary><see cref="FunctionContext.Items"/> key under which the resolved role is stored for downstream handlers.</summary>
    public const string ResolvedRoleKey = "OperatorApiRole";

    /// <summary>Full-access service role, covering every Operator API endpoint.</summary>
    public const string PortalAccessRole = "CloudImaging.PortalAccess";

    /// <summary>Read-only service role, limited to <see cref="MediaBuilderAllowedFunctions"/>.</summary>
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
            "GetManagedWifiNetworksForMediaBuilder",
            "GetDriverSetsForMediaBuilder",
            "GetDriverSetLinksForMediaBuilder",
        };

    // Wi-Fi passwords leave the service only for USB media, so the portal's full-access role cannot read them.
    private static readonly HashSet<string> MediaBuilderOnlyFunctions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "GetManagedWifiNetworksForMediaBuilder",
            "GetDriverSetsForMediaBuilder",
            "GetDriverSetLinksForMediaBuilder",
        };

    /// <summary>Returns why <paramref name="roles"/> may not call <paramref name="functionName"/>, or null when allowed.</summary>
    public static string? Deny(IReadOnlySet<string> roles, string functionName)
    {
        bool hasPortalAccess = roles.Contains(PortalAccessRole);
        bool hasMediaBuilderAccess = roles.Contains(MediaBuilderAccessRole);
        if (!hasPortalAccess && !hasMediaBuilderAccess)
        {
            return "Caller does not hold any recognised Operator API app role.";
        }
        if (MediaBuilderOnlyFunctions.Contains(functionName) && !hasMediaBuilderAccess)
        {
            return $"'{functionName}' is only available to CloudImaging.MediaBuilderAccess.";
        }
        if (!hasPortalAccess && !MediaBuilderAllowedFunctions.Contains(functionName))
        {
            return $"CloudImaging.MediaBuilderAccess does not permit access to '{functionName}'. CloudImaging.PortalAccess is required.";
        }
        return null;
    }

    /// <summary>Validates the resolved role has access to the invoked function, or short-circuits with 403.</summary>
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
        var denial = Deny(roles, functionName);
        if (denial is not null)
        {
            await WriteForbiddenAsync(context, denial);
            return;
        }

        context.Items[ResolvedRoleKey] = roles.Contains(PortalAccessRole) ? PortalAccessRole : MediaBuilderAccessRole;
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
