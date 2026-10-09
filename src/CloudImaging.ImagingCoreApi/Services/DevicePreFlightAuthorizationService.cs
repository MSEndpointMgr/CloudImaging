using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Runs parallel Microsoft Graph queries against Autopilot V1 and Intune Corporate Identifiers
/// to determine whether a device is authorized to begin an imaging session (FR-026, T134).
///
/// Decision table:
///   Autopilot V1 match  → <see cref="PreFlightAuthorizationResult.MatchedAutopilotV1"/>
///   Corporate ID match  → <see cref="PreFlightAuthorizationResult.MatchedCorporateIdentifier"/>
///   Neither match       → <see cref="PreFlightAuthorizationResult.NotAuthorized"/>
///   Requirement off     → <see cref="PreFlightAuthorizationResult.Skipped"/>
/// </summary>
public sealed partial class DevicePreFlightAuthorizationService
{
    private readonly GraphServiceClient _graphClient;
    private readonly CorporateIdentifierGraphClient _corporateIdentifierClient;
    private readonly ILogger<DevicePreFlightAuthorizationService> _logger;

    /// <param name="graphClient">Microsoft Graph client used for Autopilot V1 lookups.</param>
    /// <param name="corporateIdentifierClient">Graph client used for Intune Corporate Identifier lookups.</param>
    /// <param name="logger">Logger for this service.</param>
    public DevicePreFlightAuthorizationService(
        GraphServiceClient graphClient,
        CorporateIdentifierGraphClient corporateIdentifierClient,
        ILogger<DevicePreFlightAuthorizationService> logger)
    {
        _graphClient = graphClient;
        _corporateIdentifierClient = corporateIdentifierClient;
        _logger = logger;
    }

    /// <summary>
    /// Looks the device up in Autopilot and Corporate Identifiers. Returns
    /// <see cref="PreFlightAuthorizationResult.Skipped"/> without calling Graph when pre-flight or
    /// its Autopilot presence requirement is off.
    /// </summary>
    public async Task<PreFlightAuthorizationResult> EvaluateAsync(
        DeviceRegistrationPayload registration,
        PortalConfiguration config,
        CancellationToken ct = default)
    {
        if (!config.DevicePreFlightAuthorizationEnabled || !config.PreFlightRequireAutopilotPresence)
        {
            LogPreFlightDisabled(_logger, registration.SerialNumber);
            return PreFlightAuthorizationResult.Skipped;
        }

        LogPreFlightStarted(_logger, registration.SerialNumber);

        // Run both Graph queries in parallel for efficiency
        var autopilotTask = CheckAutopilotAsync(registration.SerialNumber, ct);
        var corpIdentifierTask = CheckCorporateIdentifiersAsync(
            registration.SerialNumber, registration.Manufacturer, registration.Model, ct);

        await Task.WhenAll(autopilotTask, corpIdentifierTask);

        if (await autopilotTask)
        {
            LogAutopilotMatch(_logger, registration.SerialNumber);
            return PreFlightAuthorizationResult.MatchedAutopilotV1;
        }

        if (await corpIdentifierTask)
        {
            LogCorpIdentifierMatch(_logger, registration.SerialNumber);
            return PreFlightAuthorizationResult.MatchedCorporateIdentifier;
        }

        LogPreFlightNotAuthorized(_logger, registration.SerialNumber);
        return PreFlightAuthorizationResult.NotAuthorized;
    }

    // ── Graph query helpers ──────────────────────────────────────────────────

    private async Task<bool> CheckAutopilotAsync(string serialNumber, CancellationToken ct)
    {
        try
        {
            var result = await _graphClient.DeviceManagement
                .WindowsAutopilotDeviceIdentities
                .GetAsync(req =>
                {
                    req.QueryParameters.Filter = BuildAutopilotSerialFilter(serialNumber);
                    req.QueryParameters.Top = 1;
                }, ct);

            return result?.Value?.Any(device =>
                string.Equals(device.SerialNumber, serialNumber, StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception ex)
        {
            LogGraphQueryError(_logger, ex, "Autopilot V1", serialNumber);
            return false;
        }
    }

    private async Task<bool> CheckCorporateIdentifiersAsync(
        string serialNumber, string manufacturer, string model, CancellationToken ct)
    {
        try
        {
            return await _corporateIdentifierClient.ExistsAsync(manufacturer, model, serialNumber, ct);
        }
        catch (Exception ex)
        {
            LogGraphQueryError(_logger, ex, "Corporate Identifiers", serialNumber);
            return false;
        }
    }

    internal static string BuildAutopilotSerialFilter(string serialNumber) =>
        $"serialNumber eq '{serialNumber.Trim().Replace("'", "''", StringComparison.Ordinal)}'";

    // ── Structured logging ────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot presence requirement off: skipping lookup for device {SerialNumber}.")]
    private static partial void LogPreFlightDisabled(ILogger logger, string serialNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting pre-flight authorization for device {SerialNumber}.")]
    private static partial void LogPreFlightStarted(ILogger logger, string serialNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {SerialNumber} matched Autopilot V1 — authorized.")]
    private static partial void LogAutopilotMatch(ILogger logger, string serialNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {SerialNumber} matched Corporate Identifiers — authorized.")]
    private static partial void LogCorpIdentifierMatch(ILogger logger, string serialNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Device {SerialNumber} not found in Autopilot V1 or Corporate Identifiers — not authorized.")]
    private static partial void LogPreFlightNotAuthorized(ILogger logger, string serialNumber);

    [LoggerMessage(Level = LogLevel.Error, Message = "Microsoft Graph query failed for {QueryType} (device {SerialNumber}).")]
    private static partial void LogGraphQueryError(ILogger logger, Exception ex, string queryType, string serialNumber);
}
