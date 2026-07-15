using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;
using System.Net;

namespace CloudImaging.ImagingCoreApi.Middleware;

/// <summary>RFC 7807 ProblemDetails unhandled-exception middleware for ImagingCoreApi.</summary>
public sealed class ProblemDetailsMiddleware : IFunctionsWorkerMiddleware
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
            _logger.LogError(ex, "Unhandled exception in {FunctionName}", context.FunctionDefinition.Name);

            var request = await context.GetHttpRequestDataAsync();
            if (request is null) return;

            var response = request.CreateResponse(HttpStatusCode.InternalServerError);
            response.Headers.Add("Content-Type", "application/problem+json");
            await response.WriteStringAsync(
                """{"type":"https://cloudimaging.io/errors/internal-error","title":"An unexpected error occurred.","status":500,"detail":"An internal error occurred. Check Application Insights for correlation details."}""");
            context.GetInvocationResult().Value = response;
        }
    }
}
