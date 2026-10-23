using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using CloudImaging.Contracts.Models.SetupProfiles;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Setup;

/// <summary>How loading the session's setup profile ended.</summary>
public enum SetupProfileLoadStatus
{
    /// <summary>No profile applies to this device.</summary>
    None,

    /// <summary>The profile was downloaded and verified.</summary>
    Loaded,

    /// <summary>The session is already ending; follow the normal terminal-state handling.</summary>
    PipelineEnding,

    /// <summary>The profile could not be loaded; the session fails before the disk is touched.</summary>
    Failed,
}

/// <summary>The loaded profile, or why there is none.</summary>
public sealed record SetupProfileLoadResult(SetupProfileLoadStatus Status, SetupProfileDescriptor? Descriptor, SetupProfileContent? Content, string? FailureMessage);

/// <summary>
/// Fetches the session's setup profile descriptor, downloads the version document from its
/// short-lived URL without the device token, and checks length and SHA-256 before parsing. Three
/// attempts in total across descriptor and download; a rate-limited answer waits as told and
/// counts as an attempt. The document stays in memory until Prepare setup runs.
/// </summary>
public sealed partial class SetupProfileContentLoader(
    DeviceGatewayApiClient gateway,
    HttpClient download,
    ILogger logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    /// <summary>Attempts across descriptor and download before the session fails.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Largest version document the Client accepts.</summary>
    public const long MaxContentBytes = 4 * 1024 * 1024;

    /// <summary>Shown and reported when the profile could not be loaded.</summary>
    public const string DownloadFailedMessage = "The setup profile for this device could not be downloaded. Check the device's network connection and start imaging again.";

    /// <summary>Shown and reported when the document is newer than this Client understands.</summary>
    public const string NewerClientMessage = "This setup profile needs a newer Cloud Imaging Client. Update the boot image and start imaging again.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>Loads the profile pinned on <paramref name="sessionId"/>.</summary>
    public async Task<SetupProfileLoadResult> LoadAsync(Guid sessionId, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            SetupProfileFetch fetch;
            try
            {
                fetch = await gateway.GetSetupProfileAsync(sessionId, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                LogAttemptFailed(logger, attempt, ex.Message);
                await WaitBeforeRetryAsync(attempt, RetryDelay * attempt, ct);
                continue;
            }

            switch (fetch.Status)
            {
                case SetupProfileFetchStatus.None:
                    LogNoProfile(logger);
                    return new SetupProfileLoadResult(SetupProfileLoadStatus.None, null, null, null);
                case SetupProfileFetchStatus.PipelineEnding:
                    return new SetupProfileLoadResult(SetupProfileLoadStatus.PipelineEnding, null, null, null);
                case SetupProfileFetchStatus.RateLimited:
                    LogAttemptFailed(logger, attempt, "rate limited");
                    await WaitBeforeRetryAsync(attempt, fetch.RetryAfter ?? RetryDelay, ct);
                    continue;
                case SetupProfileFetchStatus.Failed:
                    LogAttemptFailed(logger, attempt, "the Device Gateway could not return the setup profile");
                    await WaitBeforeRetryAsync(attempt, RetryDelay * attempt, ct);
                    continue;
            }

            var descriptor = fetch.Descriptor!;
            var bytes = await DownloadAsync(descriptor, attempt, ct);
            if (bytes is null)
            {
                await WaitBeforeRetryAsync(attempt, RetryDelay * attempt, ct);
                continue;
            }

            SetupProfileContent? content;
            try
            {
                content = JsonSerializer.Deserialize<SetupProfileContent>(bytes, JsonOptions);
            }
            catch (JsonException ex)
            {
                // The hash matched, so retrying would download the same document again.
                LogUnreadable(logger, ex.Message);
                return Failed(DownloadFailedMessage);
            }

            if (content is null)
            {
                return Failed(DownloadFailedMessage);
            }
            if (content.SchemaVersion != SetupProfileContent.CurrentSchemaVersion)
            {
                LogNewerSchema(logger, content.SchemaVersion);
                return Failed(NewerClientMessage);
            }

            LogLoaded(logger, descriptor.ProfileName, descriptor.Version, content.RegistryValues.Count, content.Scripts.Count, content.AnswerFile is not null);
            return new SetupProfileLoadResult(SetupProfileLoadStatus.Loaded, descriptor, content, null);
        }

        return Failed(DownloadFailedMessage);

        static SetupProfileLoadResult Failed(string message) => new(SetupProfileLoadStatus.Failed, null, null, message);
    }

    /// <summary>The document's bytes when length and hash match the descriptor; otherwise null.</summary>
    private async Task<byte[]?> DownloadAsync(SetupProfileDescriptor descriptor, int attempt, CancellationToken ct)
    {
        if (descriptor.ContentSizeBytes <= 0 || descriptor.ContentSizeBytes > MaxContentBytes
            || !Uri.TryCreate(descriptor.ContentUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            LogAttemptFailed(logger, attempt, "the descriptor is not usable");
            return null;
        }

        try
        {
            // A fresh request without the device token: the URL carries its own read permission.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogDownloadFailed(logger, url.Host, (int)response.StatusCode);
                return null;
            }

            // Read one byte past the expected size, so a larger document is caught without reading all of it.
            var limit = (int)descriptor.ContentSizeBytes + 1;
            var buffer = new byte[limit];
            var read = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                int n;
                while (read < limit && (n = await stream.ReadAsync(buffer.AsMemory(read, limit - read), ct)) > 0)
                {
                    read += n;
                }
            }

            var hash = Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, read)));
            if (read != descriptor.ContentSizeBytes || !string.Equals(hash, descriptor.ContentSha256, StringComparison.OrdinalIgnoreCase))
            {
                LogMismatch(logger, url.Host, read, descriptor.ContentSizeBytes);
                return null;
            }
            return buffer[..read];
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            LogDownloadError(logger, url.Host, ex.Message);
            return null;
        }
    }

    private async Task WaitBeforeRetryAsync(int attempt, TimeSpan wait, CancellationToken ct)
    {
        if (attempt < MaxAttempts)
        {
            await _delay(wait, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No setup profile applies to this device.")]
    private static partial void LogNoProfile(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup profile {ProfileName} version {Version} loaded: {RegistryValues} registry values, {Scripts} scripts, answer file {HasAnswerFile}.")]
    private static partial void LogLoaded(ILogger logger, string profileName, int version, int registryValues, int scripts, bool hasAnswerFile);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup profile attempt {Attempt} failed: {Reason}")]
    private static partial void LogAttemptFailed(ILogger logger, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup profile download from {Host} returned HTTP {StatusCode}.")]
    private static partial void LogDownloadFailed(ILogger logger, string host, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup profile download from {Host} failed: {Reason}")]
    private static partial void LogDownloadError(ILogger logger, string host, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup profile from {Host} does not match its descriptor: {Read} bytes read, {Expected} expected, or the hash differs.")]
    private static partial void LogMismatch(ILogger logger, string host, int read, long expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup profile document could not be read: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup profile document has schema version {SchemaVersion}, which this Client does not support.")]
    private static partial void LogNewerSchema(ILogger logger, int schemaVersion);
}
