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
///   Pre-flight disabled → <see cref="PreFlightAuthorizationResult.Skipped"/>
/// </summary>
public sealed partial class DevicePreFlightAuthorizationService
{
    private readonly GraphServiceClient _graphClient;
    private readonly PortalConfigurationRepository _configRepo;
    private readonly ILogger<DevicePreFlightAuthorizationService> _logger;

    public DevicePreFlightAuthorizationService(
        GraphServiceClient graphClient,
        PortalConfigurationRepository configRepo,
        ILogger<DevicePreFlightAuthorizationService> logger)
    {
        _graphClient = graphClient;
        _configRepo = configRepo;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates whether the device identified by <paramref name="registration"/> is authorized
    /// to begin an imaging session.  Returns immediately with <see cref="PreFlightAuthorizationResult.Skipped"/>
    /// when pre-flight authorization is disabled in <see cref="PortalConfiguration"/>.
    /// </summary>
    public async Task<PreFlightAuthorizationResult> EvaluateAsync(
        DeviceRegistrationPayload registration,
        CancellationToken ct = default)
    {
        var config = await _configRepo.GetAsync(ct);
        if (!config.DevicePreFlightAuthorizationEnabled)
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
                    req.QueryParameters.Filter = $"contains(serialNumber,'{EscapeFilter(serialNumber)}')";
                    req.QueryParameters.Top = 1;
                }, ct);

            return result?.Value?.Count > 0;
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
            // Intune importedWindowsAutopilotDeviceIdentities is the Graph v1.0 path for
            // pre-enrolled corporate identifiers (importedDeviceIdentities beta path maps here).
            var result = await _graphClient.DeviceManagement
                .ImportedWindowsAutopilotDeviceIdentities
                .GetAsync(req =>
                {
                    req.QueryParameters.Filter = $"contains(serialNumber,'{EscapeFilter(serialNumber)}')";
                    req.QueryParameters.Top = 1;
                }, ct);

            return result?.Value?.Count > 0;
        }
        catch (Exception ex)
        {
            LogGraphQueryError(_logger, ex, "Corporate Identifiers", serialNumber);
            return false;
        }
    }

    /// <summary>Minimal OData filter escaping — replaces single quotes to prevent injection.</summary>
    private static string EscapeFilter(string value) => value.Replace("'", "''");

    // ── Structured logging ────────────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Information, Message = "Pre-flight authorization disabled — skipping for device {SerialNumber}.")]
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
