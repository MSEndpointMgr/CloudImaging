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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PortalConfigurationRepository _repo;
    private readonly ILogger<PortalConfigurationFunctions> _logger;

    /// <param name="repo">Portal configuration repository.</param>
    /// <param name="logger">Logger for this function group.</param>
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
        await response.WriteStringAsync(JsonSerializer.Serialize(config, JsonOptions), context.CancellationToken);
        return response;
    }

    /// <summary>Replaces the portal configuration with the supplied payload.</summary>
    [Function(nameof(PutPortalConfiguration))]
    public async Task<HttpResponseData> PutPortalConfiguration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/portal-configuration")] HttpRequestData req,
        FunctionContext context)
    {
        PortalConfiguration? config;
        string? changedBy;
        try
        {
            using var document = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);
            config = document.RootElement.Deserialize<PortalConfiguration>(JsonOptions);
            changedBy = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("changedBy", out var by) && by.ValueKind == JsonValueKind.String
                ? by.GetString()
                : null;
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

        if (!config.IsPreFlightConfigurationValid())
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Select at least one pre-flight requirement, or turn pre-flight authorization off.", context.CancellationToken);
            return bad;
        }

        if (config.AutopilotPendingExpiryDays is < 1 or > 90)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("AutopilotPendingExpiryDays must be between 1 and 90.", context.CancellationToken);
            return bad;
        }

        if (config.AutopilotRetentionDays is < 30 or > 3650)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("AutopilotRetentionDays must be between 30 and 3650.", context.CancellationToken);
            return bad;
        }

        if (config.SetupProfileVersionRetention is < PortalConfiguration.MinSetupProfileVersionRetention or > PortalConfiguration.MaxSetupProfileVersionRetention)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Keep between 5 and 100 versions per profile.", context.CancellationToken);
            return bad;
        }

        // Who changed retention and when are set here, never taken from the request.
        var existing = await _repo.GetAsync(context.CancellationToken);
        var retentionChanged = existing.SetupProfileVersionRetention != config.SetupProfileVersionRetention;
        config = With(config, retentionChanged ? DateTimeOffset.UtcNow : existing.SetupProfileVersionRetentionChangedAt, retentionChanged ? changedBy : existing.SetupProfileVersionRetentionChangedBy);
        if (retentionChanged)
        {
            LogRetentionChanged(_logger, existing.SetupProfileVersionRetention, config.SetupProfileVersionRetention, changedBy ?? "unknown");
        }

        await _repo.UpsertAsync(config, context.CancellationToken);
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    private static PortalConfiguration With(PortalConfiguration c, DateTimeOffset? retentionChangedAt, string? retentionChangedBy) => new()
    {
        DevicePreFlightAuthorizationEnabled = c.DevicePreFlightAuthorizationEnabled,
        PreFlightRequireAutopilotPresence = c.PreFlightRequireAutopilotPresence,
        PreFlightRequireUefiFirmware = c.PreFlightRequireUefiFirmware,
        PreFlightRequireSecureBoot = c.PreFlightRequireSecureBoot,
        PreFlightRequireTpm20 = c.PreFlightRequireTpm20,
        SasTokenUrlExpiryMinutes = c.SasTokenUrlExpiryMinutes,
        BootImageSasExpiryMinutes = c.BootImageSasExpiryMinutes,
        CertValidityPeriodDays = c.CertValidityPeriodDays,
        ClockSkewToleranceSeconds = c.ClockSkewToleranceSeconds,
        SessionHistoryRetentionDays = c.SessionHistoryRetentionDays,
        UpdateCheckEnabled = c.UpdateCheckEnabled,
        AutopilotRegistrationEnabled = c.AutopilotRegistrationEnabled,
        AutopilotGroupTagRequired = c.AutopilotGroupTagRequired,
        AutopilotPendingExpiryDays = c.AutopilotPendingExpiryDays,
        AutopilotRetentionDays = c.AutopilotRetentionDays,
        SetupProfileVersionRetention = c.SetupProfileVersionRetention,
        SetupProfileVersionRetentionChangedAt = retentionChangedAt,
        SetupProfileVersionRetentionChangedBy = retentionChangedBy,
        LastModifiedAt = c.LastModifiedAt,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup profile version retention changed from {Previous} to {Current} by {ChangedBy}.")]
    private static partial void LogRetentionChanged(ILogger logger, int previous, int current, string changedBy);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid JSON in PutPortalConfiguration request body.")]
    private static partial void LogInvalidJson(ILogger logger, Exception ex);
}
