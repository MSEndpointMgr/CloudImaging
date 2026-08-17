using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Proxy endpoints that forward partitioning-scheme requests to the internal Imaging Core API
/// after role enforcement has already been applied by middleware. Mirrors
/// <see cref="PortalConfigurationFunctions"/>.
/// </summary>
public sealed partial class PartitioningSchemeFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ImagingCoreClient _coreClient;
    private readonly ILogger<PartitioningSchemeFunctions> _logger;

    public PartitioningSchemeFunctions(
        ImagingCoreClient coreClient,
        ILogger<PartitioningSchemeFunctions> logger)
    {
        _coreClient = coreClient;
        _logger = logger;
    }

    /// <summary>Returns the current partitioning scheme.</summary>
    [Function(nameof(GetPartitioningScheme))]
    public async Task<HttpResponseData> GetPartitioningScheme(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "partitioning-scheme")] HttpRequestData req,
        FunctionContext context)
    {
        var coreResponse = await _coreClient.GetPartitioningSchemeAsync(context.CancellationToken);
        var response = req.CreateResponse(coreResponse.StatusCode);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            await coreResponse.Content.ReadAsStringAsync(context.CancellationToken),
            context.CancellationToken);
        return response;
    }

    /// <summary>Replaces the partitioning scheme (Administrator only — enforced by portal server).</summary>
    [Function(nameof(PutPartitioningScheme))]
    public async Task<HttpResponseData> PutPartitioningScheme(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "partitioning-scheme")] HttpRequestData req,
        FunctionContext context)
    {
        PartitioningScheme? scheme;
        try
        {
            scheme = await JsonSerializer.DeserializeAsync<PartitioningScheme>(
                req.Body,
                JsonOptions,
                context.CancellationToken);
        }
        catch (JsonException ex)
        {
            LogInvalidJson(_logger, ex);
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON payload.", context.CancellationToken);
            return bad;
        }

        if (scheme is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Empty payload.", context.CancellationToken);
            return bad;
        }

        var coreResponse = await _coreClient.PutPartitioningSchemeAsync(scheme, context.CancellationToken);
        if (!coreResponse.IsSuccessStatusCode)
        {
            var err = req.CreateResponse(coreResponse.StatusCode);
            await err.WriteStringAsync(
                await coreResponse.Content.ReadAsStringAsync(context.CancellationToken),
                context.CancellationToken);
            return err;
        }

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid JSON in PutPartitioningScheme request body.")]
    private static partial void LogInvalidJson(ILogger logger, Exception ex);
}
