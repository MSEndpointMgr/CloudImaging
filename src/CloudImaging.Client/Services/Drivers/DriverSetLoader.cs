using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using CloudImaging.Contracts.Models.Drivers;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services.Drivers;

/// <summary>How loading the driver set ended.</summary>
public enum DriverSetLoadStatus
{
    /// <summary>A descriptor is loaded (components, or a skip reason).</summary>
    Loaded,

    /// <summary>No driver work for this session: the step is not shown.</summary>
    NotShown,

    /// <summary>The session is ending.</summary>
    PipelineEnding,

    /// <summary>The descriptor could not be read after 3 attempts.</summary>
    Failed,
}

/// <summary>Result of <see cref="DriverSetLoader.LoadAsync"/>.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="Descriptor">The descriptor when loaded.</param>
public sealed record DriverSetLoadResult(DriverSetLoadStatus Status, SessionDriverSetDescriptor? Descriptor);

/// <summary>Result of one component download.</summary>
/// <param name="Succeeded">True when the file is on disk and its SHA-256 matched.</param>
/// <param name="Reason">One sentence for a failure.</param>
public sealed record DriverDownloadResult(bool Succeeded, string? Reason);

/// <summary>
/// Reads the session's driver set from the Device Gateway and downloads its files
/// (contracts/client-install-drivers.md section 2). Both retry 3 times with waits of 2 and 4
/// seconds; a rate-limited answer counts as an attempt and waits as told. Files stream to disk
/// through a 1 MB buffer while their SHA-256 is computed, and a read link that is refused (403)
/// or expires within 2 minutes is replaced once.
/// </summary>
public sealed partial class DriverSetLoader
{
    /// <summary>The descriptor could not be read.</summary>
    public const string DescriptorFailedMessage = "The driver set could not be downloaded.";

    /// <summary>Downloaded bytes did not match the descriptor's SHA-256.</summary>
    public const string ChecksumMismatchMessage = "The downloaded file does not match its checksum.";

    /// <summary>The file could not be downloaded.</summary>
    public const string DownloadFailedMessage = "The file could not be downloaded.";

    /// <summary>Download buffer; nothing beyond it is held in memory.</summary>
    public const int BufferSize = 1024 * 1024;

    /// <summary>A link expiring within this is replaced before the download starts.</summary>
    public static readonly TimeSpan LinkRefreshMargin = TimeSpan.FromMinutes(2);

    private const int MaxAttempts = 3;
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];
    private readonly DeviceGatewayApiClient _gateway;
    private readonly HttpClient _download;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="gateway">Device Gateway client (session token).</param>
    /// <param name="download">Client for the read links, without the session token.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="time">Clock.</param>
    /// <param name="delay">Wait between attempts.</param>
    public DriverSetLoader(DeviceGatewayApiClient gateway, HttpClient download, ILogger logger, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _gateway = gateway;
        _download = download;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Reads the descriptor.</summary>
    public async Task<DriverSetLoadResult> LoadAsync(Guid sessionId, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            DriverSetFetch fetch;
            try
            {
                fetch = await _gateway.GetDriverSetAsync(sessionId, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                LogDescriptorAttemptFailed(_logger, attempt, ex.Message);
                fetch = new DriverSetFetch(DriverSetFetchStatus.Failed, null, null);
            }

            switch (fetch.Status)
            {
                case DriverSetFetchStatus.Loaded:
                    return new DriverSetLoadResult(DriverSetLoadStatus.Loaded, fetch.Descriptor);
                case DriverSetFetchStatus.None:
                    return new DriverSetLoadResult(DriverSetLoadStatus.NotShown, null);
                case DriverSetFetchStatus.PipelineEnding:
                    return new DriverSetLoadResult(DriverSetLoadStatus.PipelineEnding, null);
            }

            if (attempt < MaxAttempts)
            {
                await _delay(fetch.RetryAfter ?? Backoff[attempt - 1], ct);
            }
        }

        return new DriverSetLoadResult(DriverSetLoadStatus.Failed, null);
    }

    /// <summary>Downloads one component to <paramref name="destinationPath"/>, verifying its SHA-256.</summary>
    public async Task<DriverDownloadResult> DownloadAsync(Guid sessionId, SessionDriverComponent component, string destinationPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(component);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var url = component.DownloadUrl;
        var refreshed = false;
        if (component.DownloadUrlExpiresAt - _time.GetUtcNow() < LinkRefreshMargin)
        {
            (url, refreshed) = (await RefreshAsync(sessionId, component.Index, ct) ?? url, true);
        }

        var reason = DownloadFailedMessage;
        var attempt = 1;
        while (attempt <= MaxAttempts)
        {
            try
            {
                using var response = await _download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.StatusCode == HttpStatusCode.Forbidden && !refreshed)
                {
                    // An expired or revoked link: replace it once without spending an attempt.
                    refreshed = true;
                    url = await RefreshAsync(sessionId, component.Index, ct) ?? url;
                    continue;
                }

                response.EnsureSuccessStatusCode();
                if (await WriteAsync(response, destinationPath, component.Sha256, component.SizeBytes, ct))
                {
                    return new DriverDownloadResult(true, null);
                }

                reason = ChecksumMismatchMessage;
                LogChecksumMismatch(_logger, component.ComponentId, attempt);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                reason = DownloadFailedMessage;
                LogDownloadAttemptFailed(_logger, component.ComponentId, attempt, ex.Message);
            }

            TryDelete(destinationPath);
            if (attempt < MaxAttempts)
            {
                await _delay(Backoff[attempt - 1], ct);
            }

            attempt++;
        }

        return new DriverDownloadResult(false, reason);
    }

    private async Task<string?> RefreshAsync(Guid sessionId, int index, CancellationToken ct)
    {
        try
        {
            return (await _gateway.RefreshDriverComponentLinkAsync(sessionId, index, ct))?.DownloadUrl;
        }
        catch (HttpRequestException ex)
        {
            LogLinkRefreshFailed(_logger, index, ex.Message);
            return null;
        }
    }

    // A file longer than the descriptor lists can never match its hash, so reading stops there
    // instead of filling the Windows volume.
    private static async Task<bool> WriteAsync(HttpResponseMessage response, string destinationPath, string expectedSha256, long expectedSize, CancellationToken ct)
    {
        if (expectedSize > 0 && response.Content.Headers.ContentLength is { } announced && announced != expectedSize)
        {
            return false;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (expectedSize > 0 && total > expectedSize)
                {
                    return false;
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        return string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            // A partial file that cannot be deleted is overwritten by the next attempt.
            LogPartialFileKept(_logger, path, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Partial driver file {Path} could not be deleted: {Reason}")]
    private static partial void LogPartialFileKept(ILogger logger, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver set request attempt {Attempt} failed: {Reason}")]
    private static partial void LogDescriptorAttemptFailed(ILogger logger, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver component {ComponentId} did not match its checksum (attempt {Attempt}).")]
    private static partial void LogChecksumMismatch(ILogger logger, string componentId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver component {ComponentId} download attempt {Attempt} failed: {Reason}")]
    private static partial void LogDownloadAttemptFailed(ILogger logger, string componentId, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The read link of driver component {Index} could not be replaced: {Reason}")]
    private static partial void LogLinkRefreshFailed(ILogger logger, int index, string reason);
}
