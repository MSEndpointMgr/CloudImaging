using System.Globalization;
using System.IO;
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
    public static string CurrentLogFilePath =>
        Path.Combine(LogDirectory, $"cloud-imaging-client-{DateTime.UtcNow:yyyyMMdd}.log");

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
