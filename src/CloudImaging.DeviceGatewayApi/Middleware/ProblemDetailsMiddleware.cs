using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;
using System.Net;

namespace CloudImaging.DeviceGatewayApi.Middleware;

/// <summary>
/// Catches unhandled exceptions and returns RFC 7807 ProblemDetails responses.
/// Ensures no stack traces or internal details are exposed to clients (FR-009, FR-002).
/// </summary>
public sealed partial class ProblemDetailsMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<ProblemDetailsMiddleware> _logger;

    public ProblemDetailsMiddleware(ILogger<ProblemDetailsMiddleware> logger) => _logger = logger;

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            LogUnhandledException(_logger, ex, context.FunctionDefinition.Name);

            var request = await context.GetHttpRequestDataAsync();
            if (request is null) return;

            var response = request.CreateResponse(HttpStatusCode.InternalServerError);
            response.Headers.Add("Content-Type", "application/problem+json");
            await response.WriteStringAsync(
                """{"type":"https://cloudimaging.io/errors/internal-error","title":"An unexpected error occurred.","status":500,"detail":"An internal error occurred. Check Application Insights for correlation details."}""");
            context.GetInvocationResult().Value = response;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception in {FunctionName}")]
    private static partial void LogUnhandledException(ILogger logger, Exception ex, string functionName);
}
