using System.Net;
using System.Security.Cryptography.X509Certificates;
using CloudImaging.DeviceGatewayApi.Security;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Middleware;

/// <summary>
/// Validates the client mTLS certificate forwarded by Azure Application Gateway /
/// Azure Front Door via the <c>X-ARR-ClientCert</c> HTTP header (FR-069).
///
/// Validation rules:
///   1. The header must be present and non-empty.
///   2. The header value must be a valid Base-64-encoded DER certificate.
///   3. The SHA-1 thumbprint of the presented certificate must match the active
///      thumbprint returned by <see cref="BootMediaCertificateThumbprintCache"/>.
///   4. The certificate must not be expired at the time of the request.
///
/// On failure the middleware short-circuits with HTTP 401.
///
/// Every Device Gateway function is validated, including the session bootstrap
/// endpoint (CreateSession). The boot-media certificate is embedded in the WIM and
/// loaded by the Cloud Imaging Client at WinPE startup, so it is presented on the
/// very first request; per FR-069 mTLS authenticates ALL client requests to the
/// Device Gateway API. The device-session *token* is a separate credential — that,
/// and only that, is exempted for CreateSession (see DeviceSessionTokenValidationMiddleware).
/// </summary>
public sealed partial class MtlsCertificateValidationMiddleware : IFunctionsWorkerMiddleware
{
    private const string ClientCertHeader = "X-ARR-ClientCert";

    /// <summary>
    /// <see cref="FunctionContext.Items"/> key under which the validated client certificate is
    /// stored for downstream functions (e.g. CreateSession proof-of-possession verification).
    /// </summary>
    public const string ClientCertificateItemKey = "ClientCertificate";

    private readonly BootMediaCertificateThumbprintCache _thumbprintCache;
    private readonly ILogger<MtlsCertificateValidationMiddleware> _logger;

    public MtlsCertificateValidationMiddleware(
        BootMediaCertificateThumbprintCache thumbprintCache,
        ILogger<MtlsCertificateValidationMiddleware> logger)
    {
        _thumbprintCache = thumbprintCache;
        _logger          = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = await context.GetHttpRequestDataAsync();
        if (httpContext is null)
        {
            await next(context);
            return;
        }

        // 1. Require X-ARR-ClientCert header
        if (!httpContext.Headers.TryGetValues(ClientCertHeader, out var headerValues))
        {
            LogHeaderMissing(_logger, ClientCertHeader);
            await WriteUnauthorizedAsync(context, httpContext, "Client certificate is required.");
            return;
        }

        string certHeader = headerValues.First().Trim();
        if (string.IsNullOrEmpty(certHeader))
        {
            LogHeaderEmpty(_logger, ClientCertHeader);
            await WriteUnauthorizedAsync(context, httpContext, "Client certificate is required.");
            return;
        }

        // 2. Parse the Base-64 DER certificate
        byte[] certBytes;
        try
        {
            certBytes = Convert.FromBase64String(certHeader);
        }
        catch (FormatException)
        {
            LogInvalidBase64(_logger, ClientCertHeader);
            await WriteUnauthorizedAsync(context, httpContext, "Invalid client certificate encoding.");
            return;
        }

        X509Certificate2 cert;
        try
        {
            // X509CertificateLoader is the .NET 9+ replacement for the obsolete
            // X509Certificate2(byte[]) constructor (SYSLIB0057).
            cert = X509CertificateLoader.LoadCertificate(certBytes);
        }
        catch (Exception ex)
        {
            LogCertParseError(_logger, ex);
            await WriteUnauthorizedAsync(context, httpContext, "Invalid client certificate.");
            return;
        }

        // 3. Reject expired certificates
        var now = DateTimeOffset.UtcNow;
        if (now < cert.NotBefore || now > cert.NotAfter)
        {
            LogCertExpired(_logger, cert.Thumbprint);
            await WriteUnauthorizedAsync(context, httpContext, "Client certificate is expired.");
            return;
        }

        // 4. Compare thumbprint against active cached thumbprint
        string? activeThumbprint = await _thumbprintCache.GetThumbprintAsync(context.CancellationToken);
        if (activeThumbprint is null)
        {
            LogNoActiveCert(_logger);
            await WriteUnauthorizedAsync(context, httpContext, "No active boot media certificate.");
            return;
        }

        bool thumbprintMatch = string.Equals(
            cert.Thumbprint,
            activeThumbprint,
            StringComparison.OrdinalIgnoreCase);

        if (!thumbprintMatch)
        {
            LogThumbprintMismatch(_logger, cert.Thumbprint, activeThumbprint);
            await WriteUnauthorizedAsync(context, httpContext, "Client certificate is not authorized.");
            return;
        }

        // Store thumbprint and the parsed certificate for downstream functions. The certificate's
        // public key is used by CreateSession to verify the application-layer proof-of-possession
        // signature (FR-069), so it must survive to the function invocation.
        context.Items["ClientCertThumbprint"] = cert.Thumbprint;
        context.Items[ClientCertificateItemKey] = cert;

        await next(context);
    }

    private static async Task WriteUnauthorizedAsync(
        FunctionContext context,
        HttpRequestData req,
        string message)
    {
        var response = req.CreateResponse(HttpStatusCode.Unauthorized);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(
            $"{{\"type\":\"https://tools.ietf.org/html/rfc7807\",\"title\":\"Unauthorized\",\"status\":401,\"detail\":\"{message}\"}}",
            context.CancellationToken);
        context.GetInvocationResult().Value = response;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "mTLS: {Header} header missing.")]
    private static partial void LogHeaderMissing(ILogger logger, string header);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mTLS: {Header} header is empty.")]
    private static partial void LogHeaderEmpty(ILogger logger, string header);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mTLS: {Header} header is not valid Base-64.")]
    private static partial void LogInvalidBase64(ILogger logger, string header);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mTLS: Failed to parse client certificate.")]
    private static partial void LogCertParseError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mTLS: Certificate is expired or not yet valid. Thumbprint={Thumbprint}")]
    private static partial void LogCertExpired(ILogger logger, string thumbprint);

    [LoggerMessage(Level = LogLevel.Error, Message = "mTLS: No active boot-media certificate is registered.")]
    private static partial void LogNoActiveCert(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "mTLS: Thumbprint mismatch. Presented={Presented}, Active={Active}")]
    private static partial void LogThumbprintMismatch(ILogger logger, string presented, string active);
}

