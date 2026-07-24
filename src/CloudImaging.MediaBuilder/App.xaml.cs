using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using CloudImaging.MediaBuilder.Logging;
using CloudImaging.MediaBuilder.Services;
using CloudImaging.MediaBuilder.ViewModels;
using CloudImaging.MediaBuilder.Views;
using Microsoft.Extensions.Logging;

namespace CloudImaging.MediaBuilder;

public partial class App : System.Windows.Application
{
    private Serilog.Core.Logger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Surface any unhandled failure instead of the process dying silently
        // (e.g. when launched elevated). Wire these before any startup work.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(args.ExceptionObject as Exception, "AppDomain.UnhandledException");
        DispatcherUnhandledException += (_, args) =>
        {
            ReportFatal(args.Exception, "DispatcherUnhandledException");
            args.Handled = true;
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportFatal(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };

        try
        {
            _logger = LoggingConfiguration.CreateLogger();
            Serilog.Log.Logger = _logger;

            var config = LoadConfiguration();
            var loggerFactory = LoggingConfiguration.CreateLoggerFactory(_logger);

            var authService = new EntraAuthenticationService(
                config.ClientId,
                config.TenantId,
                config.OperatorApiScope,
                loggerFactory.CreateLogger<EntraAuthenticationService>());

            var httpClient = new HttpClient();
            if (!string.IsNullOrEmpty(config.OperatorApiBaseUrl))
                httpClient.BaseAddress = new Uri(config.OperatorApiBaseUrl);

            var operatorApiClient = new OperatorApiClient(
                httpClient, loggerFactory.CreateLogger<OperatorApiClient>());

            var genService = new BootImageGenerationService(
                loggerFactory.CreateLogger<BootImageGenerationService>(), operatorApiClient);

            var mainWindow = new MainWindow();
            mainWindow.NavigateTo(BuildSignInView(mainWindow, authService, genService, loggerFactory));
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            ReportFatal(ex, "OnStartup");
            Shutdown(-1);
            return;
        }

        base.OnStartup(e);
    }

    /// <summary>
    /// Logs a fatal startup/runtime error and shows it to the user so the app never
    /// dies without a trace (important when launched with elevated privileges).
    /// </summary>
    private void ReportFatal(Exception? ex, string source)
    {
        try
        {
            Serilog.Log.Logger?.Fatal(ex, "Unhandled exception ({Source}).", source);
            _logger?.Dispose();
        }
        catch
        {
            // Never let error reporting itself crash shutdown.
        }

        System.Windows.MessageBox.Show(
            $"Cloud Imaging Media Builder failed to start.\n\n{ex?.Message}\n\n{ex}",
            "Cloud Imaging Media Builder",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Dispose();
        base.OnExit(e);
    }

    // ── Navigation factories ──────────────────────────────────────────────────

    private static SignInView BuildSignInView(
        MainWindow window,
        EntraAuthenticationService auth,
        BootImageGenerationService gen,
        ILoggerFactory lf)
    {
        var view = new SignInView();
        view.DataContext = new SignInViewModel(
            auth,
            () => window.NavigateTo(BuildOperationSelectionView(window, auth, gen, lf)));
        return view;
    }

    private static OperationSelectionView BuildOperationSelectionView(
        MainWindow window,
        EntraAuthenticationService auth,
        BootImageGenerationService gen,
        ILoggerFactory lf)
    {
        var view = new OperationSelectionView();
        view.DataContext = new OperationSelectionViewModel(op =>
        {
            if (op == "GenerateBootImage")
                window.NavigateTo(BuildGenerateBootImageView(window, auth, gen, lf));
            // PrepareUSB reserved for a future sprint
        });
        return view;
    }

    private static GenerateBootImageView BuildGenerateBootImageView(
        MainWindow window,
        EntraAuthenticationService auth,
        BootImageGenerationService gen,
        ILoggerFactory lf)
    {
        var view = new GenerateBootImageView();
        view.DataContext = new GenerateBootImageViewModel(
            gen,
            auth,
            () => window.NavigateTo(BuildOperationSelectionView(window, auth, gen, lf)));
        return view;
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    private static MediaBuilderConfig LoadConfiguration()
    {
        // The committed appsettings.json ships with empty placeholders so no environment
        // specific values leak into a public release. Developers put real dev values in
        // appsettings.Local.json, which is git-ignored and overlaid here at runtime.
        var config = ReadConfigFile("appsettings.json");
        config = Overlay(config, ReadConfigFile("appsettings.Local.json"));
        return config;
    }

    private static MediaBuilderConfig ReadConfigFile(string fileName)
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(settingsPath))
            return new MediaBuilderConfig(string.Empty, string.Empty, string.Empty, string.Empty);

        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var clientId = string.Empty;
            var tenantId = string.Empty;
            var scope = string.Empty;
            if (root.TryGetProperty("EntraId", out var entraId))
            {
                clientId = entraId.TryGetProperty("ClientId", out var ci) ? ci.GetString() ?? string.Empty : string.Empty;
                tenantId = entraId.TryGetProperty("TenantId", out var ti) ? ti.GetString() ?? string.Empty : string.Empty;
                scope = entraId.TryGetProperty("OperatorApiScope", out var os) ? os.GetString() ?? string.Empty : string.Empty;
            }

            var baseUrl = root.TryGetProperty("OperatorApi", out var oa) && oa.TryGetProperty("BaseUrl", out var bu)
                ? bu.GetString() ?? string.Empty
                : string.Empty;

            return new MediaBuilderConfig(clientId, tenantId, scope, baseUrl);
        }
        catch
        {
            return new MediaBuilderConfig(string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    // Non-empty values from the overlay replace the corresponding base values.
    private static MediaBuilderConfig Overlay(MediaBuilderConfig baseConfig, MediaBuilderConfig overlay) =>
        new(
            string.IsNullOrWhiteSpace(overlay.ClientId) ? baseConfig.ClientId : overlay.ClientId,
            string.IsNullOrWhiteSpace(overlay.TenantId) ? baseConfig.TenantId : overlay.TenantId,
            string.IsNullOrWhiteSpace(overlay.OperatorApiScope) ? baseConfig.OperatorApiScope : overlay.OperatorApiScope,
            string.IsNullOrWhiteSpace(overlay.OperatorApiBaseUrl) ? baseConfig.OperatorApiBaseUrl : overlay.OperatorApiBaseUrl);

    private sealed record MediaBuilderConfig(
        string ClientId,
        string TenantId,
        string OperatorApiScope,
        string OperatorApiBaseUrl);
}
