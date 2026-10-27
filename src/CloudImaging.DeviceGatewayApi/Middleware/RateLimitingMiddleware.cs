using System.Collections.Concurrent;
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace CloudImaging.DeviceGatewayApi.Middleware;

/// <summary>
/// Rate limiting middleware.
///
/// Per token: 10 calls per 30-second window per device-session token (or Autopilot status token)
/// (FR-018).
///
/// Per client address: the endpoints called without a token (session bootstrap, Autopilot
/// availability and submission, managed Wi-Fi, the boot image update check) are limited to
/// <see cref="TokenFreeMaxCallsPerWindow"/> calls per <see cref="TokenFreeWindowDuration"/> per
/// client IP address. The limit is generous because a whole site often reaches the gateway
/// through one NAT address, and every device makes several of these calls when it starts.
///
/// Returns HTTP 429 with Retry-After header when a limit is exceeded. Counters are per instance.
/// </summary>
public sealed partial class RateLimitingMiddleware : IFunctionsWorkerMiddleware
{
    /// <summary>Maximum calls allowed per token within <see cref="WindowDuration"/>.</summary>
    public const int MaxCallsPerWindow = 10;

    /// <summary>Length of the per-token rate-limit window.</summary>
    public static readonly TimeSpan WindowDuration = TimeSpan.FromSeconds(30);

    /// <summary>Maximum token-free calls allowed per client address within <see cref="TokenFreeWindowDuration"/>.</summary>
    public const int TokenFreeMaxCallsPerWindow = 300;

    /// <summary>Length of the per-address window for token-free calls.</summary>
    public static readonly TimeSpan TokenFreeWindowDuration = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, WindowCounter> _counters = new();
    private static long _lastSweepTicks = DateTimeOffset.UtcNow.UtcTicks;

    /// <summary>Function names not subject to per-token rate limiting (called without a token).</summary>
    public static readonly IReadOnlyCollection<string> ExemptFunctionNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CreateSession" };

    private static readonly HashSet<string> ExemptFunctions =
        new(StringComparer.OrdinalIgnoreCase) { "CreateSession" };

    private readonly ILogger<RateLimitingMiddleware> _logger;

    /// <param name="logger">Logger for rate-limit rejections.</param>
    public RateLimitingMiddleware(ILogger<RateLimitingMiddleware> logger) => _logger = logger;

    /// <summary>Counts the call against its token's or client address's window and short-circuits with 429 if the limit is exceeded.</summary>
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        SweepIdleCounters();
        var functionName = context.FunctionDefinition.Name;

        if (DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Contains(functionName))
        {
            var request = await context.GetHttpRequestDataAsync();
            var address = request is null ? null : ClientAddress(request);
            if (address is not null)
            {
                var counter = _counters.GetOrAdd("ip:" + address, _ => new WindowCounter());
                var (allowed, retryAfterSeconds) = counter.TryIncrement(TokenFreeMaxCallsPerWindow, TokenFreeWindowDuration);
                if (!allowed)
                {
                    LogAddressRateLimitExceeded(_logger, address, functionName);
                    await RejectAsync(context, request!, retryAfterSeconds, TokenFreeMaxCallsPerWindow, TokenFreeWindowDuration);
                    return;
                }
            }
        }

        if (ExemptFunctions.Contains(functionName))
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

        var tokenCounter = _counters.GetOrAdd(keyStr, _ => new WindowCounter());
        var (tokenAllowed, tokenRetryAfterSeconds) = tokenCounter.TryIncrement(MaxCallsPerWindow, WindowDuration);

        if (!tokenAllowed)
        {
            LogRateLimitExceeded(_logger, keyStr[..8]);
            var request = await context.GetHttpRequestDataAsync();
            if (request is not null)
            {
                await RejectAsync(context, request, tokenRetryAfterSeconds, MaxCallsPerWindow, WindowDuration);
                return;
            }
        }

        await next(context);
    }

    /// <summary>
    /// The caller's IP address from the last <c>X-Forwarded-For</c> entry, which the App Service
    /// front end appends (an entry the caller sent itself comes earlier). Port removed; null when absent.
    /// </summary>
    public static string? ClientAddress(HttpRequestData request)
    {
        if (!request.Headers.TryGetValues("X-Forwarded-For", out var values))
        {
            return null;
        }
        var last = string.Join(',', values).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrEmpty(last))
        {
            return null;
        }
        if (IPAddress.TryParse(last, out var plain))
        {
            return plain.ToString();
        }

        // "a.b.c.d:port" or "[v6]:port"
        var portSeparator = last.LastIndexOf(':');
        var host = portSeparator > 0 ? last[..portSeparator].Trim('[', ']') : last;
        return IPAddress.TryParse(host, out var withPort) ? withPort.ToString() : null;
    }

    private static async Task RejectAsync(FunctionContext context, HttpRequestData request, int retryAfterSeconds, int maxCalls, TimeSpan window)
    {
        var response = request.CreateResponse(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(
            $$"""{"type":"https://cloudimaging.io/errors/rate-limit-exceeded","title":"Too Many Requests","status":429,"detail":"Rate limit of {{maxCalls}} calls per {{(int)window.TotalSeconds}} seconds exceeded.","retryAfterSeconds":{{retryAfterSeconds}}}""");
        context.GetInvocationResult().Value = response;
    }

    // Counters for tokens and addresses that went quiet would otherwise stay in memory for the life of the instance.
    private static void SweepIdleCounters()
    {
        var now = DateTimeOffset.UtcNow;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - last < SweepInterval.Ticks || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, last) != last)
        {
            return;
        }
        foreach (var (key, counter) in _counters)
        {
            if (now - counter.WindowStart > SweepInterval)
            {
                _counters.TryRemove(key, out _);
            }
        }
    }

    private sealed class WindowCounter
    {
        private readonly object _lock = new();
        private DateTimeOffset _windowStart = DateTimeOffset.UtcNow;
        private int _count;

        public DateTimeOffset WindowStart
        {
            get { lock (_lock) { return _windowStart; } }
        }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit exceeded for client address {Address} on {FunctionName}")]
    private static partial void LogAddressRateLimitExceeded(ILogger logger, string address, string functionName);
}
