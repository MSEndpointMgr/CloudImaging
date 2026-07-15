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
    /// Builds and returns the Serilog logger to be used as the application log.
    /// Caller is responsible for assigning to <see cref="Log.Logger"/> and
    /// disposing on application exit.
    /// </summary>
    public static Serilog.Core.Logger CreateLogger()
    {
        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudImaging", "Client", "Logs");

        Directory.CreateDirectory(logDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path:           Path.Combine(logDirectory, "cloud-imaging-client-.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 10 * 1024 * 1024,    // 10 MB
                retainedFileCountLimit: 5,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>
    /// Returns an <see cref="ILoggerFactory"/> backed by the Serilog rolling file
    /// logger, suitable for passing to <see cref="Microsoft.Extensions.Logging"/>.
    /// </summary>
    public static ILoggerFactory CreateLoggerFactory(Serilog.Core.Logger serilogLogger) =>
        LoggerFactory.Create(builder => builder.AddSerilog(serilogLogger, dispose: false));
}
