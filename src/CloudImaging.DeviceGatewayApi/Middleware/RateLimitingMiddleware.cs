using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Middleware;

/// <summary>
/// Per-session rate limiting middleware: 10 calls per 30-second sliding window per device-session token.
/// The public session bootstrap endpoint is exempt (FR-018).
/// Returns HTTP 429 with Retry-After header when limit is exceeded.
/// </summary>
public sealed partial class RateLimitingMiddleware : IFunctionsWorkerMiddleware
{
    public const int MaxCallsPerWindow = 10;
    public static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, WindowCounter> _counters = new();

    public static readonly IReadOnlyCollection<string> ExemptFunctionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CreateSession" };

    private static readonly HashSet<string> ExemptFunctions =
        new(StringComparer.OrdinalIgnoreCase) { "CreateSession" };

    private readonly ILogger<RateLimitingMiddleware> _logger;

    public RateLimitingMiddleware(ILogger<RateLimitingMiddleware> logger) => _logger = logger;

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        if (ExemptFunctions.Contains(context.FunctionDefinition.Name))
        {
            await next(context);
            return;
        }

        // Token already extracted by DeviceSessionTokenValidationMiddleware
        if (!context.Items.TryGetValue("BearerToken", out var tokenObj) || tokenObj is not string token)
        {
            await next(context);
            return;
        }

        var key = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(token));
        var keyStr = Convert.ToHexString(key);

        var counter = _counters.GetOrAdd(keyStr, _ => new WindowCounter());
        var (allowed, retryAfterSeconds) = counter.TryIncrement(MaxCallsPerWindow, WindowDuration);

        if (!allowed)
        {
            LogRateLimitExceeded(_logger, keyStr[..8]);
            var request = await context.GetHttpRequestDataAsync();
            if (request is not null)
            {
                var response = request.CreateResponse(HttpStatusCode.TooManyRequests);
                response.Headers.Add("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                response.Headers.Add("Content-Type", "application/problem+json");
                await response.WriteStringAsync(
                    $$"""{"type":"https://cloudimaging.io/errors/rate-limit-exceeded","title":"Too Many Requests","status":429,"detail":"Rate limit of {{MaxCallsPerWindow}} calls per {{(int)WindowDuration.TotalSeconds}} seconds exceeded.","retryAfterSeconds":{{retryAfterSeconds}}}""");
                context.GetInvocationResult().Value = response;
                return;
            }
        }

        await next(context);
    }

    private sealed class WindowCounter
    {
        private readonly object _lock = new();
        private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
        private int _count;

        /// <returns>(allowed, retryAfterSeconds)</returns>
        public (bool Allowed, int RetryAfterSeconds) TryIncrement(int max, TimeSpan window)
        {
            lock (_lock)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _windowStart >= window)
                {
                    _windowStart = now;
                    _count = 0;
                }

                if (_count >= max)
                {
                    var remaining = window - (now - _windowStart);
                    return (false, (int)Math.Ceiling(remaining.TotalSeconds));
                }

                _count++;
                return (true, 0);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded for session token (key prefix {Prefix})")]
    private static partial void LogRateLimitExceeded(ILogger logger, string prefix);
}
