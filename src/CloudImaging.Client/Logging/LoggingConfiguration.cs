using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace CloudImaging.Client.Logging;

/// <summary>
/// Configures rolling file logging for the Cloud Imaging Client WPF application.
/// Files are written to the user's LocalApplicationData so they survive WinPE
/// reboot transitions and do not require elevated permission (FR-066).
/// Max 10 MB per file, 5 files retained, no network dependency.
/// </summary>
internal static class LoggingConfiguration
{
    /// <summary>
    /// Directory the rolling log files are written to. Exposed so
    /// <see cref="Services.LogUploadService"/> can locate the current log file without
    /// depending on Serilog's internal file-rolling implementation.
    /// </summary>
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CloudImaging", "Client", "Logs");

    /// <summary>
    /// The path of today's rolling log file — the same naming convention passed to
    /// <see cref="LoggerConfiguration.WriteTo"/> below (<c>cloud-imaging-client-{Date}.log</c>,
    /// daily rolling interval).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="DateTime.Now"/> (local time), NOT <see cref="DateTime.UtcNow"/> — the
    /// Serilog.Sinks.File <c>File()</c> overload used below has no <c>useUtcTimestamp</c> option
    /// in this version and always rolls/names daily files by local time internally. Computing
    /// this path from UTC instead would silently disagree with the file Serilog is actually
    /// writing to whenever the machine's local time zone isn't UTC (common even in WinPE, which
    /// keeps whatever time zone the image is configured with), making LogViewerWindow report
    /// "No log file found yet" even though logging is working correctly.
    /// </remarks>
    public static string CurrentLogFilePath =>
        Path.Combine(LogDirectory, $"cloud-imaging-client-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>
    /// Builds and returns the Serilog logger to be used as the application log.
    /// Caller is responsible for assigning to <see cref="Log.Logger"/> and
    /// disposing on application exit.
    /// </summary>
    public static Serilog.Core.Logger CreateLogger()
    {
        Directory.CreateDirectory(LogDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path:           Path.Combine(LogDirectory, "cloud-imaging-client-.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024,    // 10 MB
                retainedFileCountLimit: 5,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                // UTF-8 *with* a byte-order mark. Log messages contain non-ASCII characters
                // (arrows, ellipses, em dashes), and Serilog's default is UTF-8 without a BOM —
                // so Notepad and other viewers on a machine whose ANSI code page is Windows-1252
                // silently decode the file as legacy ANSI and render "→" as "â†'". The BOM makes
                // the encoding unambiguous for anything that opens the downloaded file locally,
                // where HTTP Content-Type is no longer available to disambiguate it.
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                // Allows LogUploadService to open the same file for read (FileShare.ReadWrite)
                // while Serilog still holds it open for writing, so a failure mid-session can be
                // uploaded without closing/reopening the application's own logger.
                shared: true)
            .CreateLogger();
    }


    /// <summary>
    /// Returns an <see cref="ILoggerFactory"/> backed by the Serilog rolling file
    /// logger, suitable for passing to <see cref="Microsoft.Extensions.Logging"/>.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(Serilog.Core.Logger serilogLogger) =>
        LoggerFactory.Create(builder => builder.AddSerilog(serilogLogger, dispose: false));
}
