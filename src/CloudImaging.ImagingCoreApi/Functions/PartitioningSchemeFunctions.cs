using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// HTTP-triggered Azure Functions for the global disk partitioning scheme.
/// These endpoints are internal-only (VNet-integrated, not publicly reachable).
/// </summary>
public sealed partial class PartitioningSchemeFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PartitioningSchemeRepository _repo;
    private readonly ILogger<PartitioningSchemeFunctions> _logger;

    public PartitioningSchemeFunctions(
        PartitioningSchemeRepository repo,
        ILogger<PartitioningSchemeFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    /// <summary>Returns the current partitioning scheme.</summary>
    [Function(nameof(GetPartitioningScheme))]
    public async Task<HttpResponseData> GetPartitioningScheme(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/partitioning-scheme")] HttpRequestData req,
        FunctionContext context)
    {
        var scheme = await _repo.GetAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(scheme, JsonOptions), context.CancellationToken);
        return response;
    }

    /// <summary>Replaces the partitioning scheme with the supplied payload.</summary>
    [Function(nameof(PutPartitioningScheme))]
    public async Task<HttpResponseData> PutPartitioningScheme(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/partitioning-scheme")] HttpRequestData req,
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

        if (scheme is null || scheme.Partitions is null || scheme.Partitions.Count == 0)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("At least one partition definition is required.", context.CancellationToken);
            return bad;
        }

        if (!ValidationHelpers.IsValidPartitioningScheme(scheme, out var validationError))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(validationError, context.CancellationToken);
            return bad;
        }

        await _repo.UpsertAsync(scheme, context.CancellationToken);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid JSON in PutPartitioningScheme request body.")]
    private static partial void LogInvalidJson(ILogger logger, Exception ex);
}

/// <summary>Shared validation for a submitted <see cref="PartitioningScheme"/>.</summary>
internal static class ValidationHelpers
{
    public static bool IsValidPartitioningScheme(PartitioningScheme scheme, out string error)
    {
        var types = scheme.Partitions.Select(p => p.PartitionType).ToList();

        if (types.Count != types.Distinct().Count())
        {
            error = "Each partition type may only appear once in the scheme.";
            return false;
        }

        var requiredTypes = new[]
        {
            Contracts.Enums.PartitionType.EfiSystem,
            Contracts.Enums.PartitionType.Msr,
            Contracts.Enums.PartitionType.Windows,
            Contracts.Enums.PartitionType.Recovery,
        };

        if (requiredTypes.Except(types).Any())
        {
            error = "The scheme must include all four partition types: EfiSystem, Msr, Windows, Recovery.";
            return false;
        }

        if (scheme.Partitions.Any(p => p.PartitionType != Contracts.Enums.PartitionType.Windows && p.SizeMb <= 0))
        {
            error = "EfiSystem, Msr, and Recovery partitions must have a size greater than 0 MB.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
