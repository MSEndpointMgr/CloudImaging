using System.Windows;
using CloudImaging.MediaBuilder.Logging;

namespace CloudImaging.MediaBuilder;

public partial class App : System.Windows.Application
{
    private Serilog.Core.Logger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        _logger = LoggingConfiguration.CreateLogger();
        Serilog.Log.Logger = _logger;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Dispose();
        base.OnExit(e);
    }
}
