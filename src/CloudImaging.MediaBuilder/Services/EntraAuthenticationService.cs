using Microsoft.Identity.Client;
using Microsoft.Extensions.Logging;
using System.IO;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Orchestrates Entra ID interactive sign-in for the Media Builder desktop app (T063, FR-052).
/// Uses MSAL PublicClientApplication with desktop interactive flow.
/// The access token is used by <see cref="OperatorApiClient"/> to call the Operator API.
/// </summary>
public sealed partial class EntraAuthenticationService
{
    private readonly IPublicClientApplication? _msal;
    private readonly string[] _scopes;
    private readonly ILogger<EntraAuthenticationService> _logger;
    private readonly string? _configurationError;

    private AuthenticationResult? _lastResult;

    public EntraAuthenticationService(
        string clientId,
        string tenantId,
        string operatorApiScope,
        ILogger<EntraAuthenticationService> logger)
    {
        _scopes = [operatorApiScope];
        _logger = logger;

        // Validate configuration up front. Missing values (e.g. an unfilled
        // appsettings.json) must NOT crash startup — the app still opens and the
        // problem is reported when the user attempts to sign in.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(clientId)) missing.Add("EntraId:ClientId");
        if (string.IsNullOrWhiteSpace(tenantId)) missing.Add("EntraId:TenantId");
        if (string.IsNullOrWhiteSpace(operatorApiScope)) missing.Add("EntraId:OperatorApiScope");

        if (missing.Count > 0)
        {
            _configurationError =
                "Entra ID sign-in is not configured. Missing settings: " +
                string.Join(", ", missing) +
                ". Update appsettings.json next to the application.";
            LogNotConfigured(_logger, string.Join(", ", missing));
            return;
        }

        _msal = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithDefaultRedirectUri()
            .WithLogging(
                (level, message, containsPii) => LogMsal(_logger, level, message),
                Microsoft.Identity.Client.LogLevel.Info,
                enablePiiLogging: false,
                enableDefaultPlatformLogging: true)
            .Build();

        // Do NOT persist the token cache to disk: the technician must sign in on every
        // launch. MSAL keeps an in-memory cache for the lifetime of this process (so token
        // refresh works while the app runs), but nothing survives a restart. Remove any
        // cache file written by earlier builds so stale tokens can't silently sign in.
        MsalTokenCache.PurgeLegacyCache();
    }

    /// <summary>Sign-in result returned to the view model.</summary>
    public sealed record SignInResult(bool Success, string? UserPrincipalName, string? ErrorMessage);

    /// <summary>
    /// Attempts a silent token acquisition first; falls back to interactive browser sign-in.
    /// </summary>
    public async Task<SignInResult> SignInAsync(CancellationToken ct = default)
    {
        if (_msal is null)
            return new SignInResult(false, null, _configurationError ?? "Entra ID sign-in is not configured.");

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
                // Interactive sign-in required. Replace MSAL's plain default browser
                // landing page with a branded Cloud Imaging success/error page.
                result = await _msal.AcquireTokenInteractive(_scopes)
                    .WithPrompt(Prompt.SelectAccount)
                    .WithSystemWebViewOptions(new SystemWebViewOptions
                    {
                        HtmlMessageSuccess = BrandedRedirectPages.Success,
                        HtmlMessageError = BrandedRedirectPages.Error,
                    })
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
        if (_msal is null || _lastResult is null) return null;
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

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Entra ID not configured; missing settings: {Missing}. Sign-in is disabled until appsettings.json is completed.")]
    private static partial void LogNotConfigured(ILogger logger, string missing);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Trace, Message = "MSAL [{Level}]: {Message}")]
    private static partial void LogMsal(ILogger logger, Microsoft.Identity.Client.LogLevel level, string message);
}

/// <summary>
/// Self-contained, branded HTML shown in the system browser after the interactive
/// Entra ID redirect completes, replacing MSAL's plain default landing page.
/// The markup is fully inline (no external CSS/JS/fonts/images) because the page is
/// served by MSAL's loopback listener and must render without network access.
/// Adapts automatically to the OS light/dark theme via <c>prefers-color-scheme</c>.
/// </summary>
internal static class BrandedRedirectPages
{
    private const string Style = """
        <style>
          :root { color-scheme: light dark; }
          * { box-sizing: border-box; }
          html, body { height: 100%; margin: 0; }
          body {
            font-family: 'Segoe UI', system-ui, -apple-system, Roboto, Helvetica, Arial, sans-serif;
            display: flex; align-items: center; justify-content: center;
            padding: 24px; background: #f3f4f6; color: #1f2937;
          }
          .card {
            width: 100%; max-width: 440px; background: #ffffff;
            border: 1px solid #e5e7eb; border-radius: 12px;
            box-shadow: 0 10px 30px rgba(0,0,0,0.08);
            padding: 40px 36px; text-align: center;
          }
          .badge {
            width: 64px; height: 64px; margin: 0 auto 20px;
            border-radius: 50%; display: flex; align-items: center; justify-content: center;
          }
          .badge.ok { background: rgba(16,185,129,0.12); }
          .badge.err { background: rgba(220,38,38,0.12); }
          .badge svg { width: 34px; height: 34px; }
          .brand {
            font-size: 12px; letter-spacing: .12em; text-transform: uppercase;
            color: #6b7280; margin-bottom: 6px;
          }
          h1 { font-size: 22px; line-height: 1.3; margin: 0 0 12px; font-weight: 600; }
          p { font-size: 14px; line-height: 1.6; color: #4b5563; margin: 0 0 12px; }
          .hint {
            margin-top: 20px; padding-top: 16px; border-top: 1px solid #e5e7eb;
            font-size: 12px; color: #9ca3af;
          }
          code {
            display: block; margin-top: 8px; padding: 10px 12px; border-radius: 8px;
            background: #f9fafb; border: 1px solid #e5e7eb; color: #b91c1c;
            font-family: 'Cascadia Code', Consolas, monospace; font-size: 12px;
            word-break: break-word; text-align: left;
          }
          @media (prefers-color-scheme: dark) {
            body { background: #0b1220; color: #e5e7eb; }
            .card { background: #111827; border-color: #1f2937; box-shadow: 0 10px 30px rgba(0,0,0,0.5); }
            .brand { color: #9ca3af; }
            p { color: #cbd5e1; }
            .hint { border-top-color: #1f2937; color: #6b7280; }
            code { background: #0b1220; border-color: #1f2937; color: #f87171; }
          }
        </style>
        """;

    public static readonly string Success = $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Cloud Imaging &middot; Signed in</title>
          {Style}
        </head>
        <body>
          <main class="card" role="status">
            <div class="badge ok" aria-hidden="true">
              <svg viewBox="0 0 24 24" fill="none" stroke="#10b981" stroke-width="2.5"
                   stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg>
            </div>
            <div class="brand">Cloud Imaging Media Builder</div>
            <h1>You're signed in</h1>
            <p>Authentication completed successfully. Return to the Cloud Imaging Media Builder to continue &mdash; you can close this browser tab.</p>
            <p class="hint">For your security, don't share the contents of this page or the address bar.</p>
          </main>
        </body>
        </html>
        """;

    // MSAL substitutes {0} = error code and {1} = error description into this page via
    // string.Format. Because the shared CSS contains literal '{' / '}', the raw template is
    // escaped (braces doubled) and the placeholder is re-inserted so string.Format renders
    // the CSS braces literally and only substitutes the error details.
    private const string ErrorTemplate = $"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Cloud Imaging &middot; Sign-in failed</title>
          {Style}
        </head>
        <body>
          <main class="card" role="alert">
            <div class="badge err" aria-hidden="true">
              <svg viewBox="0 0 24 24" fill="none" stroke="#dc2626" stroke-width="2.5"
                   stroke-linecap="round" stroke-linejoin="round"><path d="M12 9v4"/><path d="M12 17h.01"/><circle cx="12" cy="12" r="9"/></svg>
            </div>
            <div class="brand">Cloud Imaging Media Builder</div>
            <h1>Sign-in didn't complete</h1>
            <p>Something went wrong while signing you in. Close this tab and try again from the Media Builder. If the problem persists, contact your administrator.</p>
            <code>__ERROR_DETAIL__</code>
            <p class="hint">For your security, don't share the contents of this page or the address bar.</p>
          </main>
        </body>
        </html>
        """;

    public static readonly string Error = ErrorTemplate
        .Replace("{", "{{")
        .Replace("}", "}}")
        .Replace("__ERROR_DETAIL__", "{0}: {1}");
}

/// <summary>
/// The Media Builder deliberately does NOT persist MSAL tokens to disk: the technician
/// must sign in on every launch. This helper removes any token cache file written by
/// earlier builds so previously stored tokens can no longer silently sign in.
/// </summary>
internal static class MsalTokenCache
{
    private static readonly string CacheFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudImaging",
        "MediaBuilder",
        "msalcache.bin3");

    public static void PurgeLegacyCache()
    {
        try
        {
            if (File.Exists(CacheFilePath))
                File.Delete(CacheFilePath);
        }
        catch (IOException)
        {
            // Non-fatal: a locked cache file just can't be removed right now.
        }
        catch (UnauthorizedAccessException)
        {
            // Non-fatal: insufficient permissions to delete the stale cache file.
        }
    }
}
