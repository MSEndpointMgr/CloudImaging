using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>Result of reading a Graph importedWindowsAutopilotDeviceIdentity.</summary>
public sealed record AutopilotImportStatus(string DeviceImportStatus, string? ErrorCode, string? ErrorName)
{
    /// <summary>True when the import has finished successfully.</summary>
    public bool IsComplete => string.Equals(DeviceImportStatus, "complete", StringComparison.OrdinalIgnoreCase);
    /// <summary>True when the import finished with an error.</summary>
    public bool IsError => string.Equals(DeviceImportStatus, "error", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A Graph call failed in a way the operator should see verbatim.</summary>
public sealed class AutopilotGraphException : Exception
{
    /// <param name="message">The error message.</param>
    /// <param name="statusCode">The HTTP status code returned by Graph, when known.</param>
    public AutopilotGraphException(string message, HttpStatusCode? statusCode = null)
        : base(message) => StatusCode = statusCode;

    /// <summary>Initializes a new instance with no message.</summary>
    public AutopilotGraphException()
    {
    }

    /// <param name="message">The error message.</param>
    public AutopilotGraphException(string message)
        : base(message)
    {
    }

    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception that caused this failure.</param>
    public AutopilotGraphException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The HTTP status code returned by Graph, when known.</summary>
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// Microsoft Graph calls for Windows Autopilot (v1) device import, mirroring what
/// Get-WindowsAutopilotInfo -Online does: POST an importedWindowsAutopilotDeviceIdentity, poll its
/// state until Intune finishes processing it, then delete the import record. Uses the Imaging Core
/// managed identity, which needs DeviceManagementServiceConfig.ReadWrite.All for the write calls.
/// </summary>
public class AutopilotGraphClient
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;

    /// <param name="httpClient">HttpClient configured for Microsoft Graph calls.</param>
    /// <param name="credential">Credential used to acquire Graph access tokens.</param>
    public AutopilotGraphClient(HttpClient httpClient, TokenCredential credential)
    {
        _httpClient = httpClient;
        _credential = credential;
    }

    /// <summary>True when a Windows Autopilot device with this exact serial number already exists in the tenant.</summary>
    public virtual async Task<bool> IsSerialRegisteredAsync(string serialNumber, CancellationToken ct)
    {
        // contains() is the filter the Autopilot devices endpoint reliably supports; the exact match is checked locally.
        var escaped = serialNumber.Trim().Replace("'", "''", StringComparison.Ordinal);
        var filter = Uri.EscapeDataString($"contains(serialNumber,'{escaped}')");
        using var response = await SendAsync(HttpMethod.Get, $"v1.0/deviceManagement/windowsAutopilotDeviceIdentities?$filter={filter}&$select=serialNumber", null, ct);
        await EnsureSuccessAsync(response, "look up existing Autopilot devices", ct);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.TryGetProperty("value", out var entries)
            && entries.ValueKind == JsonValueKind.Array
            && entries.EnumerateArray().Any(entry =>
                entry.TryGetProperty("serialNumber", out var serial)
                && string.Equals(serial.GetString(), serialNumber.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Submits the device for import and returns the Graph id of the import record.
    /// <paramref name="hardwareHash"/> is OA3Tool's Base64 HardwareHash, which is exactly the Edm.Binary
    /// JSON encoding Graph expects for hardwareIdentifier.
    /// </summary>
    public virtual async Task<string> ImportAsync(string serialNumber, string hardwareHash, string? groupTag, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["@odata.type"] = "#microsoft.graph.importedWindowsAutopilotDeviceIdentity",
            ["serialNumber"] = serialNumber,
            ["hardwareIdentifier"] = hardwareHash,
            ["groupTag"] = groupTag ?? string.Empty,
            ["productKey"] = string.Empty,
            ["assignedUserPrincipalName"] = string.Empty,
            ["state"] = new Dictionary<string, object?>
            {
                ["@odata.type"] = "microsoft.graph.importedWindowsAutopilotDeviceIdentityState",
                ["deviceImportStatus"] = "pending",
                ["deviceRegistrationId"] = string.Empty,
                ["deviceErrorCode"] = 0,
                ["deviceErrorName"] = string.Empty,
            },
        };

        using var response = await SendAsync(HttpMethod.Post, "v1.0/deviceManagement/importedWindowsAutopilotDeviceIdentities", JsonContent.Create(body, options: JsonOptions), ct);
        await EnsureSuccessAsync(response, "import the device into Windows Autopilot", ct);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return document.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value
            ? value
            : throw new AutopilotGraphException("Graph accepted the import but returned no import id.");
    }

    /// <summary>Reads the processing state of an import record.</summary>
    public virtual async Task<AutopilotImportStatus> GetImportStatusAsync(string importedIdentityId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"v1.0/deviceManagement/importedWindowsAutopilotDeviceIdentities/{Uri.EscapeDataString(importedIdentityId)}", null, ct);
        await EnsureSuccessAsync(response, "read the Autopilot import status", ct);

        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
        {
            return new AutopilotImportStatus("unknown", null, null);
        }

        var status = state.TryGetProperty("deviceImportStatus", out var s) ? s.GetString() ?? "unknown" : "unknown";
        var errorCode = state.TryGetProperty("deviceErrorCode", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        var errorName = state.TryGetProperty("deviceErrorName", out var n) ? n.GetString() : null;
        return new AutopilotImportStatus(status, errorCode == "0" ? null : errorCode, string.IsNullOrWhiteSpace(errorName) ? null : errorName);
    }

    /// <summary>Removes a processed import record. Missing records are ignored.</summary>
    public virtual async Task DeleteImportAsync(string importedIdentityId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"v1.0/deviceManagement/importedWindowsAutopilotDeviceIdentities/{Uri.EscapeDataString(importedIdentityId)}", null, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, "clean up the Autopilot import record", ct);
        }
    }

    /// <summary>Asks Intune to sync Autopilot devices so a new registration shows up sooner.</summary>
    public virtual async Task SyncAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "v1.0/deviceManagement/windowsAutopilotSettings/sync", null, ct);
        await EnsureSuccessAsync(response, "sync Windows Autopilot devices", ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUri, HttpContent? content, CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);
        using var request = new HttpRequestMessage(method, relativeUri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await _httpClient.SendAsync(request, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new AutopilotGraphException(
                $"Microsoft Graph denied the request to {action}. Grant the Imaging Core managed identity the DeviceManagementServiceConfig.ReadWrite.All application permission.",
                response.StatusCode);
        }

        var detail = await ReadGraphErrorAsync(response, ct);
        throw new AutopilotGraphException($"Microsoft Graph failed to {action} (HTTP {(int)response.StatusCode}){detail}", response.StatusCode);
    }

    private static async Task<string> ReadGraphErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message)
                ? $": {message.GetString()}"
                : ".";
        }
        catch (JsonException)
        {
            return ".";
        }
    }
}
