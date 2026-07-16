using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Orchestrates Entra ID interactive sign-in for the Media Builder desktop app (T063, FR-052).
/// Uses MSAL PublicClientApplication with desktop interactive flow.
/// The access token is used by <see cref="OperatorApiClient"/> to call the Operator API.
/// </summary>
public sealed partial class EntraAuthenticationService
{
    private readonly IPublicClientApplication _msal;
    private readonly string[] _scopes;
    private readonly ILogger<EntraAuthenticationService> _logger;

    private AuthenticationResult? _lastResult;

    public EntraAuthenticationService(
        string clientId,
        string tenantId,
        string operatorApiScope,
        ILogger<EntraAuthenticationService> logger)
    {
        _scopes = [operatorApiScope];
        _logger = logger;

        _msal = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithDefaultRedirectUri()
            .Build();
    }

    /// <summary>Sign-in result returned to the view model.</summary>
    public sealed record SignInResult(bool Success, string? UserPrincipalName, string? ErrorMessage);

    /// <summary>
    /// Attempts a silent token acquisition first; falls back to interactive browser sign-in.
    /// </summary>
    public async Task<SignInResult> SignInAsync(CancellationToken ct = default)
    {
        try
        {
            // Try silent first for already-cached accounts
            var accounts = await _msal.GetAccountsAsync();
            AuthenticationResult result;

            try
            {
                result = await _msal.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
                    .ExecuteAsync(ct);
            }
            catch (MsalUiRequiredException)
            {
                // Interactive sign-in required
                result = await _msal.AcquireTokenInteractive(_scopes)
                    .WithPrompt(Prompt.SelectAccount)
                    .ExecuteAsync(ct);
            }

            _lastResult = result;
            LogSignedIn(_logger, result.Account.Username);
            return new SignInResult(true, result.Account.Username, null);
        }
        catch (MsalException ex)
        {
            LogSignInFailed(_logger, ex);
            return new SignInResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Returns the most recently acquired access token, or null if not signed in.
    /// Attempts silent refresh if the cached token has expired.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_lastResult is null) return null;
        try
        {
            var accounts = await _msal.GetAccountsAsync();
            var result = await _msal.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
                .ExecuteAsync(ct);
            _lastResult = result;
            return result.AccessToken;
        }
        catch { return null; }
    }

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Signed in as {Upn}.")]
    private static partial void LogSignedIn(ILogger logger, string upn);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Entra sign-in failed.")]
    private static partial void LogSignInFailed(ILogger logger, Exception ex);
}
