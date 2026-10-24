using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
/// <param name="Status">How loading ended.</param>
/// <param name="Descriptor">The descriptor the texts came from.</param>
/// <param name="Content">The version document, holding only the scripts this device runs.</param>
/// <param name="FailureMessage">Shown and reported when loading failed.</param>
public sealed record SetupProfileLoadResult(SetupProfileLoadStatus Status, SetupProfileDescriptor? Descriptor, SetupProfileContent? Content, string? FailureMessage)
{
    /// <summary>The verified answer file text, or null when the version has none.</summary>
    public string? AnswerFileText { get; init; }
}

/// <summary>
/// Fetches the session's setup profile descriptor, downloads the version document and checks each
/// script the descriptor lists for this device and the answer file from their short-lived URLs
/// without the device token, verifying every length and SHA-256 before Format disk. Three attempts
/// in total across descriptor and downloads; a rate-limited answer waits as told and counts as an
/// attempt. Script texts are only hashed here, never kept: after the image is applied,
/// <see cref="DownloadScriptsAsync"/> fetches them again straight to the Windows volume, so the
/// Client's memory does not grow with the profile's scripts.
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

    /// <summary>Where script texts are downloaded on the applied volume; Prepare setup removes it.</summary>
    public const string TextFolder = WinPeScriptRunner.WorkFolder + @"\Texts";

    /// <summary>Shown and reported when the profile could not be loaded.</summary>
    public const string DownloadFailedMessage = "The setup profile for this device could not be downloaded. Check the device's network connection and start imaging again.";

    /// <summary>Shown and reported when the document is newer than this Client understands.</summary>
    public const string NewerClientMessage = "This setup profile needs a newer Cloud Imaging Client. Update the boot image and start imaging again.";

    private const int BufferSize = 81_920;
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>Relative path of a downloaded script text on the applied volume.</summary>
    public static string TextPath(string sha256) => $@"{TextFolder}\{sha256}.ps1";

    /// <summary>Loads the profile pinned on <paramref name="sessionId"/>.</summary>
    public async Task<SetupProfileLoadResult> LoadAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Texts verified in an earlier attempt are not checked again, so a retry only fetches what is still missing.
        var verified = new HashSet<string>(StringComparer.Ordinal);
        string? answerFileText = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            SetupProfileFetch fetch;
            try
            {
                fetch = await gateway.GetSetupProfileAsync(sessionId, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
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
            var bytes = await DownloadAsync(descriptor.ContentUrl, descriptor.ContentSizeBytes, descriptor.ContentSha256, MaxContentBytes, attempt, ct);
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

            // Only the scripts the descriptor lists run on this device; each must match the document.
            var byId = content.Scripts.Where(s => s.Id is not null).ToDictionary(s => s.Id!.Value);
            var listed = new List<SetupScript>();
            foreach (var text in descriptor.Scripts)
            {
                if (text.Id is not { } id || !byId.TryGetValue(id, out var script) || script.ContentSha256 != text.ContentSha256)
                {
                    LogDescriptorMismatch(logger, text.Id?.ToString() ?? "(none)");
                    return Failed(DownloadFailedMessage);
                }
                listed.Add(script);
            }
            if (content.AnswerFile is { } file && descriptor.AnswerFile?.ContentSha256 != file.ContentSha256)
            {
                LogDescriptorMismatch(logger, "answer file");
                return Failed(DownloadFailedMessage);
            }

            var complete = true;
            foreach (var text in descriptor.Scripts.Where(t => !verified.Contains(t.ContentSha256)))
            {
                if (!await DownloadToAsync(text.ContentUrl, text.SizeBytes, text.ContentSha256, SetupProfileContent.MaxScriptBytes, attempt, Stream.Null, ct))
                {
                    complete = false;
                    break;
                }
                verified.Add(text.ContentSha256);
            }
            if (complete && content.AnswerFile is not null && answerFileText is null)
            {
                var answer = descriptor.AnswerFile!;
                var answerBytes = await DownloadAsync(answer.ContentUrl, answer.SizeBytes, answer.ContentSha256, SetupProfileContent.MaxAnswerFileBytes, attempt, ct);
                complete = answerBytes is not null;
                answerFileText = answerBytes is null ? null : Encoding.UTF8.GetString(answerBytes);
            }
            if (!complete)
            {
                await WaitBeforeRetryAsync(attempt, RetryDelay * attempt, ct);
                continue;
            }

            content = content with { Scripts = listed };
            LogLoaded(logger, descriptor.ProfileName, descriptor.Version, content.RegistryValues.Count, content.Scripts.Count, content.AnswerFile is not null);
            return new SetupProfileLoadResult(SetupProfileLoadStatus.Loaded, descriptor, content, null) { AnswerFileText = answerFileText };
        }

        return Failed(DownloadFailedMessage);

        static SetupProfileLoadResult Failed(string message) => new(SetupProfileLoadStatus.Failed, null, null, message);
    }

    /// <summary>
    /// Downloads the scripts of a loaded profile again, straight to <paramref name="volume"/> as
    /// UTF-8 with BOM (Windows PowerShell 5.1 reads files without one as ANSI), checking each length
    /// and SHA-256 while it streams. A fresh descriptor gives new read URLs, because the first ones
    /// have expired by the time the image is applied. Returns the verified files by SHA-256; a
    /// script missing from the result fails on its own in Prepare setup. <paramref name="progress"/>
    /// gets the number of the script being downloaded and the total before each download.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> DownloadScriptsAsync(Guid sessionId, SetupProfileLoadResult loaded, ISetupFileSystem volume, Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var wanted = (loaded.Content?.Scripts ?? []).Select(s => s.ContentSha256).ToHashSet(StringComparer.Ordinal);
        if (loaded.Descriptor is not { } pinned || wanted.Count == 0)
        {
            return files;
        }

        for (var attempt = 1; attempt <= MaxAttempts && files.Count < wanted.Count; attempt++)
        {
            SetupProfileFetch fetch;
            try
            {
                fetch = await gateway.GetSetupProfileAsync(sessionId, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                LogAttemptFailed(logger, attempt, ex.Message);
                await WaitBeforeRetryAsync(attempt, RetryDelay * attempt, ct);
                continue;
            }
            if (fetch.Status == SetupProfileFetchStatus.RateLimited)
            {
                LogAttemptFailed(logger, attempt, "rate limited");
                await WaitBeforeRetryAsync(attempt, fetch.RetryAfter ?? RetryDelay, ct);
                continue;
            }
            if (fetch.Status == SetupProfileFetchStatus.Failed)
            {
                LogAttemptFailed(logger, attempt, "the Device Gateway could not return the setup profile");
                await WaitBeforeRetryAsync(attempt, RetryDelay * attempt, ct);
                continue;
            }

            // The session pins its version, so anything else means the session is ending.
            if (fetch.Status != SetupProfileFetchStatus.Pinned || fetch.Descriptor!.ContentSha256 != pinned.ContentSha256)
            {
                LogDescriptorMismatch(logger, "a version other than the one loaded before Format disk");
                return files;
            }

            foreach (var text in fetch.Descriptor.Scripts.Where(t => wanted.Contains(t.ContentSha256) && !files.ContainsKey(t.ContentSha256)).DistinctBy(t => t.ContentSha256))
            {
                var path = TextPath(text.ContentSha256);
                progress?.Invoke(files.Count + 1, wanted.Count);
                bool written;
                try
                {
                    await using var file = volume.Create(path);
                    await file.WriteAsync(Utf8Bom, ct);
                    written = await DownloadToAsync(text.ContentUrl, text.SizeBytes, text.ContentSha256, SetupProfileContent.MaxScriptBytes, attempt, file, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogTextNotWritten(logger, ex.Message);
                    written = false;
                }
                if (written)
                {
                    files[text.ContentSha256] = path;
                    continue;
                }
                try
                {
                    volume.DeleteFile(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogTextNotWritten(logger, ex.Message);
                }
            }
            if (files.Count < wanted.Count)
            {
                await WaitBeforeRetryAsync(attempt, RetryDelay * attempt, ct);
            }
        }
        return files;
    }

    /// <summary>The bytes of a small document when length and hash match what is expected; otherwise null.</summary>
    private async Task<byte[]?> DownloadAsync(string contentUrl, long expectedSize, string expectedSha256, long maxBytes, int attempt, CancellationToken ct)
    {
        using var sink = new MemoryStream((int)Math.Clamp(expectedSize + 1, 0, maxBytes + 1));
        return await DownloadToAsync(contentUrl, expectedSize, expectedSha256, maxBytes, attempt, sink, ct) ? sink.ToArray() : null;
    }

    /// <summary>
    /// Streams a download into <paramref name="sink"/> through a small buffer and returns true when
    /// its length and SHA-256 match. The caller discards what was written when it returns false.
    /// </summary>
    private async Task<bool> DownloadToAsync(string contentUrl, long expectedSize, string expectedSha256, long maxBytes, int attempt, Stream sink, CancellationToken ct)
    {
        if (expectedSize <= 0 || expectedSize > maxBytes
            || !Uri.TryCreate(contentUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            LogAttemptFailed(logger, attempt, "the descriptor is not usable");
            return false;
        }

        try
        {
            // A fresh request without the device token: the URL carries its own read permission.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogDownloadFailed(logger, url.Host, (int)response.StatusCode);
                return false;
            }

            // Read one byte past the expected size, so a larger download is caught without reading all of it.
            var limit = expectedSize + 1;
            var buffer = new byte[(int)Math.Min(BufferSize, limit)];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long read = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                int n;
                while (read < limit && (n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - read)), ct)) > 0)
                {
                    hash.AppendData(buffer, 0, n);
                    await sink.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                }
            }

            if (read != expectedSize || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                LogMismatch(logger, url.Host, read, expectedSize);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            LogDownloadError(logger, url.Host, ex.Message);
            return false;
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
    private static partial void LogMismatch(ILogger logger, string host, long read, long expected);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A setup profile script could not be written to the Windows volume: {Reason}")]
    private static partial void LogTextNotWritten(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup profile document could not be read: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup profile document has schema version {SchemaVersion}, which this Client does not support.")]
    private static partial void LogNewerSchema(ILogger logger, int schemaVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The setup profile descriptor lists {Item}, which does not match the version document.")]
    private static partial void LogDescriptorMismatch(ILogger logger, string item);
}
