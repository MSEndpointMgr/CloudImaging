using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Queries the Microsoft Graph beta endpoint used by Intune Corporate Identifiers.
/// The v1.0 importedWindowsAutopilotDeviceIdentities endpoint is an Autopilot import queue and
/// does not contain Windows manufacturer/model/serial corporate identifiers.
/// </summary>
public sealed class CorporateIdentifierGraphClient
{
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;

    public CorporateIdentifierGraphClient(HttpClient httpClient, TokenCredential credential)
    {
        _httpClient = httpClient;
        _credential = credential;
    }

    public async Task<bool> ExistsAsync(
        string manufacturer,
        string model,
        string serialNumber,
        CancellationToken ct)
    {
        var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(manufacturer, model, serialNumber));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return document.RootElement.TryGetProperty("value", out var entries)
            && entries.ValueKind == JsonValueKind.Array
            && entries.GetArrayLength() > 0;
    }

    internal static string BuildRequestUri(string manufacturer, string model, string serialNumber)
    {
        var identifier = string.Join(',', manufacturer.Trim(), model.Trim(), serialNumber.Trim());
        var escapedIdentifier = identifier.Replace("'", "''", StringComparison.Ordinal);
        var filter = $"importedDeviceIdentityType eq 'manufacturerModelSerial' and importedDeviceIdentifier eq '{escapedIdentifier}'";
        return $"beta/deviceManagement/importedDeviceIdentities?$filter={Uri.EscapeDataString(filter)}&$select=id&$top=1";
    }
}