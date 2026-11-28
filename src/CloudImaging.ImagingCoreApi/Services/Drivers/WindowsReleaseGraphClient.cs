using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>Microsoft's feature updates, or why they could not be read.</summary>
/// <param name="Releases">Releases that are still deployable; empty when <paramref name="Problem"/> is set.</param>
/// <param name="Problem">Why the list could not be read.</param>
public sealed record WindowsReleaseRead(IReadOnlyList<WindowsReleaseEntry> Releases, string? Problem);

/// <summary>Where new Windows releases come from: Microsoft's feature update catalog.</summary>
public interface IWindowsReleaseSource
{
    /// <summary>Reads the feature updates Microsoft lists. Never throws for an unreachable or refusing service.</summary>
    Task<WindowsReleaseRead> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// Reads Microsoft's feature update catalog through the Windows Updates API in Microsoft Graph
/// beta, as the Imaging Core managed identity, which needs the WindowsUpdates.Read.All
/// application permission.
/// </summary>
public sealed class WindowsReleaseGraphClient : IWindowsReleaseSource
{
    /// <summary>Feature updates only; quality updates fill the rest of the catalog.</summary>
    public const string CatalogPath = "beta/admin/windows/updates/catalog/entries?$filter=isof('microsoft.graph.windowsUpdates.featureUpdateCatalogEntry')";

    /// <summary>Shown when Graph refuses the read.</summary>
    public const string DeniedMessage = "Microsoft Graph denied reading the Windows releases. The Imaging Core managed identity needs the WindowsUpdates.Read.All permission, and the tenant a licence that includes Windows Autopatch.";

    private const string GraphHost = "graph.microsoft.com";
    private const string FeatureUpdateType = "#microsoft.graph.windowsUpdates.featureUpdateCatalogEntry";
    private const int MaxPages = 10;
    private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly TimeProvider _time;

    /// <param name="httpClient">HttpClient with the Graph base address.</param>
    /// <param name="credential">The Imaging Core managed identity.</param>
    /// <param name="time">Clock, to leave out releases no longer deployable.</param>
    public WindowsReleaseGraphClient(HttpClient httpClient, TokenCredential credential, TimeProvider time)
    {
        _httpClient = httpClient;
        _credential = credential;
        _time = time;
    }

    /// <inheritdoc/>
    public async Task<WindowsReleaseRead> ReadAsync(CancellationToken ct = default)
    {
        var releases = new List<WindowsReleaseEntry>();
        try
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext(GraphScopes), ct);
            Uri? next = new(CatalogPath, UriKind.Relative);
            for (var page = 0; next is not null && page < MaxPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return new WindowsReleaseRead([], await DescribeAsync(response, ct));
                }

                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                releases.AddRange(ParsePage(document.RootElement, _time.GetUtcNow()));
                next = NextPage(document.RootElement);
            }
        }
        catch (AuthenticationFailedException ex)
        {
            return new WindowsReleaseRead([], $"The Imaging Core managed identity could not sign in to Microsoft Graph: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new WindowsReleaseRead([], $"Microsoft Graph could not be reached: {ex.Message}");
        }

        return new WindowsReleaseRead(releases, null);
    }

    /// <summary>The feature updates of one page that are still deployable and name a Windows 10 or 11 release.</summary>
    public static IEnumerable<WindowsReleaseEntry> ParsePage(JsonElement page, DateTimeOffset now)
    {
        if (!page.TryGetProperty("value", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (Text(entry, "@odata.type") is { } type && !string.Equals(type, FeatureUpdateType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // "version" names the release; "displayName" was just "Windows 11" for 21H2.
            if (!WindowsRelease.TryParseName(Text(entry, "version"), out var release) && !WindowsRelease.TryParseName(Text(entry, "displayName"), out release))
            {
                continue;
            }

            if (Date(entry, "deployableUntilDateTime") is { } until && until <= now)
            {
                continue;
            }

            yield return new WindowsReleaseEntry(release, Text(entry, "buildNumber"), Date(entry, "releaseDateTime"));
        }
    }

    // The token only ever goes back to Graph, whatever the next link says.
    private static Uri? NextPage(JsonElement page) =>
        Text(page, "@odata.nextLink") is { } link
            && Uri.TryCreate(link, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, GraphHost, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;

    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return DeniedMessage;
        }

        var detail = ".";
        try
        {
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (document.RootElement.TryGetProperty("error", out var error) && Text(error, "message") is { Length: > 0 } message)
            {
                detail = $": {message}";
            }
        }
        catch (JsonException)
        {
            // No Graph error body; the status says enough.
        }

        return $"Microsoft Graph could not list the Windows releases (HTTP {(int)response.StatusCode}){detail}";
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var date) ? date : null;
}
