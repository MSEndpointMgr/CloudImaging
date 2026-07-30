using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CloudImaging.OperatorApi.Security;

/// <summary>
/// Options controlling how <see cref="EntraTokenValidator"/> validates incoming bearer tokens.
/// </summary>
public sealed class EntraValidationOptions
{
    /// <summary>Entra ID tenant (directory) ID that issued the tokens.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// Authority instance base URL. Defaults to the public cloud
    /// (<c>https://login.microsoftonline.com/</c>) when null or empty.
    /// </summary>
    public string? Instance { get; init; }

    /// <summary>
    /// Audiences the Operator API accepts. Normally the Operator API's own application (client) ID.
    /// The <c>api://&lt;clientId&gt;</c> form is accepted automatically in addition to the raw GUID.
    /// </summary>
    public required IReadOnlyList<string> ValidAudiences { get; init; }
}

/// <summary>
/// Validates Entra ID (Microsoft Entra / Azure AD) JWT bearer tokens end to end:
/// cryptographic signature (against the tenant's published JWKS), issuer, audience and lifetime
/// (FR-040, FR-061). Signing keys are retrieved from the tenant's OpenID Connect metadata endpoint
/// and cached/refreshed automatically, so key rollover is handled transparently.
/// </summary>
/// <remarks>
/// This replaces the previous unauthenticated <c>ReadJwtToken</c> parse, which performed no
/// signature/issuer/audience/lifetime checks and therefore allowed any well-formed (including
/// unsigned <c>alg:none</c>) token to pass.
/// </remarks>
public sealed class EntraTokenValidator
{
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly TokenValidationParameters _validationParameters;
    private readonly JwtSecurityTokenHandler _handler = new()
    {
        // Preserve original claim types (e.g. "roles") instead of mapping them to the legacy
        // ClaimTypes.* URIs, so downstream role checks can read the "roles" claim directly.
        MapInboundClaims = false,
    };

    public EntraTokenValidator(EntraValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TenantId);

        var instance = string.IsNullOrWhiteSpace(options.Instance)
            ? "https://login.microsoftonline.com/"
            : options.Instance;
        if (!instance.EndsWith('/'))
        {
            instance += "/";
        }

        var metadataAddress = $"{instance}{options.TenantId}/v2.0/.well-known/openid-configuration";
        _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });

        var audiences = new List<string>();
        foreach (var audience in options.ValidAudiences)
        {
            if (string.IsNullOrWhiteSpace(audience))
            {
                continue;
            }

            audiences.Add(audience);
            if (!audience.StartsWith("api://", StringComparison.OrdinalIgnoreCase))
            {
                audiences.Add($"api://{audience}");
            }
        }

        if (audiences.Count == 0)
        {
            throw new ArgumentException("At least one valid audience must be supplied.", nameof(options));
        }

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers =
            [
                $"https://login.microsoftonline.com/{options.TenantId}/v2.0",
                $"https://sts.windows.net/{options.TenantId}/",
            ],
            ValidateAudience = true,
            ValidAudiences = audiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            NameClaimType = "name",
            RoleClaimType = "roles",
        };
    }

    /// <summary>
    /// Validates the supplied bearer token and returns the resulting <see cref="ClaimsPrincipal"/>.
    /// Throws a <see cref="SecurityTokenException"/> (or derived type) if validation fails.
    /// </summary>
    public async Task<ClaimsPrincipal> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (!_handler.CanReadToken(token))
        {
            throw new SecurityTokenMalformedException("The bearer token is malformed.");
        }

        var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);

        var parameters = _validationParameters.Clone();
        parameters.IssuerSigningKeys = configuration.SigningKeys;

        return _handler.ValidateToken(token, parameters, out _);
    }
}
