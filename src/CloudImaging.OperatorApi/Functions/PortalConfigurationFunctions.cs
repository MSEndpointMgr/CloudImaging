using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Proxy endpoints that forward portal-configuration requests to the internal
/// Imaging Core API after role enforcement has already been applied by middleware.
/// Only CloudImaging.Administrator roles may update configuration (FR-040a).
/// </summary>
public sealed partial class PortalConfigurationFunctions
{
    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<PortalConfigurationFunctions> _logger;

    public PortalConfigurationFunctions(
        ImagingCoreClient coreClient,
        ILogger<PortalConfigurationFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>Returns the current portal configuration (PortalAccess required).</summary>
    [Function(nameof(GetPortalConfiguration))]
    public async Task<HttpResponseData> GetPortalConfiguration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "configuration")] HttpRequestData req,
        FunctionContext context)
    {
        var config = await _coreClient.GetPortalConfigurationAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(config), context.CancellationToken);
        return response;
    }

    /// <summary>
    /// Replaces the portal configuration (Administrator only — middleware enforces role).
    /// </summary>
    [Function(nameof(PutPortalConfiguration))]
    public async Task<HttpResponseData> PutPortalConfiguration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "configuration")] HttpRequestData req,
        FunctionContext context)
    {
        PortalConfiguration? config;
        try
        {
            config = await JsonSerializer.DeserializeAsync<PortalConfiguration>(
                req.Body,
                cancellationToken: context.CancellationToken);
        }
        catch (JsonException ex)
        {
            LogInvalidJson(_logger, ex);
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON payload.", context.CancellationToken);
            return bad;
        }

        if (config is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Empty payload.", context.CancellationToken);
            return bad;
        }

        await _coreClient.UpsertPortalConfigurationAsync(config, context.CancellationToken);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid JSON in PutPortalConfiguration request body.")]
    private static partial void LogInvalidJson(ILogger logger, Exception ex);
}
