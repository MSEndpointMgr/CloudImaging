using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client.Services;

/// <summary>
/// Uploads the Client's current local diagnostic log to blob storage, called best-effort by
/// <see cref="ViewModels.ImagingWorkflowViewModel"/> on any terminal imaging failure so support
/// can inspect the full log without depending on the technician retrieving it from a WinPE
/// session before reboot (local-only logging otherwise never leaves the device — see
/// <see cref="Logging.LoggingConfiguration"/>).
///
/// This is intentionally best-effort end-to-end: every failure mode here (no network, expired
/// session token, storage outage, timeout) is caught and logged as a warning, never rethrown —
/// uploading diagnostics must never delay or mask the real imaging failure being reported.
/// </summary>
public sealed partial class LogUploadService
{
    /// <summary>
    /// Upper bound on the whole upload attempt (URL request + PUT). The rolling log file is
    /// capped at 10 MB, so this comfortably covers even a slow uplink while still guaranteeing
    /// the failure Results view is never meaningfully delayed by a stalled/unreachable network.
    /// </summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;
    private readonly DeviceGatewayApiClient _gatewayClient;
    private readonly ILogger<LogUploadService> _logger;

    public LogUploadService(
        HttpClient httpClient,
        DeviceGatewayApiClient gatewayClient,
        ILogger<LogUploadService> logger)
    {
        _httpClient = httpClient;
        _gatewayClient = gatewayClient;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to upload the current rolling log file for <paramref name="sessionId"/>.
    /// Never throws — every error is logged as a warning and swallowed.
    /// </summary>
    public async Task UploadCurrentLogAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(UploadTimeout);

        try
        {
            var logFilePath = Logging.LoggingConfiguration.CurrentLogFilePath;
            if (!File.Exists(logFilePath))
            {
                LogNoFileToUpload(_logger, logFilePath);
                return;
            }

            var uploadUrlResponse = await _gatewayClient.RequestLogUploadUrlAsync(sessionId, timeoutCts.Token);
            if (uploadUrlResponse is null)
            {
                LogUploadUrlUnavailable(_logger, sessionId);
                return;
            }

            await using var fileStream = new FileStream(
                logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using var content = new StreamContent(fileStream);
            // Charset must be stated: the blob keeps this content type, and the Portal's
            // "Download logs" opens the blob URL directly, so a bare "text/plain" leaves the
            // browser to guess an encoding and render UTF-8 log text as mojibake.
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };

            using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrlResponse.UploadUrl)
            {
                Content = content,
            };
            request.Headers.Add("x-ms-blob-type", "BlockBlob");

            var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            if (response.IsSuccessStatusCode)
            {
                LogUploadSucceeded(_logger, sessionId, uploadUrlResponse.FileName);
            }
            else
            {
                LogUploadFailed(_logger, sessionId, (int)response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            LogUploadException(_logger, sessionId, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No local log file found at {LogFilePath} — skipping log upload.")]
    private static partial void LogNoFileToUpload(ILogger logger, string logFilePath);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not obtain a log upload URL for session {SessionId}.")]
    private static partial void LogUploadUrlUnavailable(ILogger logger, Guid sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Uploaded diagnostic log for session {SessionId}: {FileName}.")]
    private static partial void LogUploadSucceeded(ILogger logger, Guid sessionId, string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log upload for session {SessionId} failed with HTTP {StatusCode}.")]
    private static partial void LogUploadFailed(ILogger logger, Guid sessionId, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Log upload for session {SessionId} threw an exception.")]
    private static partial void LogUploadException(ILogger logger, Guid sessionId, Exception exception);
}
