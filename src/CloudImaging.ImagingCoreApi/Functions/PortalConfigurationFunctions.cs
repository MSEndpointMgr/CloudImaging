using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// HTTP-triggered Azure Functions for portal configuration management.
/// These endpoints are internal-only (VNet-integrated, not publicly reachable).
/// </summary>
public sealed partial class PortalConfigurationFunctions
{
    private readonly PortalConfigurationRepository _repo;
    private readonly ILogger<PortalConfigurationFunctions> _logger;

    public PortalConfigurationFunctions(
        PortalConfigurationRepository repo,
        ILogger<PortalConfigurationFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>Returns the current portal configuration.</summary>
    [Function(nameof(GetPortalConfiguration))]
    public async Task<HttpResponseData> GetPortalConfiguration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/portal-configuration")] HttpRequestData req,
        FunctionContext context)
    {
        var config = await _repo.GetAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(config), context.CancellationToken);
        return response;
    }

    /// <summary>Replaces the portal configuration with the supplied payload.</summary>
    [Function(nameof(PutPortalConfiguration))]
    public async Task<HttpResponseData> PutPortalConfiguration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/portal-configuration")] HttpRequestData req,
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

        await _repo.UpsertAsync(config, context.CancellationToken);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid JSON in PutPortalConfiguration request body.")]
    private static partial void LogInvalidJson(ILogger logger, Exception ex);
}
