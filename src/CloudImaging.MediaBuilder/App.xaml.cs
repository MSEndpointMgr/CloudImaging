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

            // Hidden elevated-worker mode: this same executable is relaunched with this
            // switch (via a UAC prompt) by BootImageGenerationService.GenerateElevatedAsync
            // when the main (non-elevated) process needs DISM image mounting, which requires
            // Administrator privileges. Runs headless — no window, no sign-in — and exits.
            if (e.Args.Length == 4 && e.Args[0] == BootImageGenerationService.ElevatedWorkerArg)
            {
                // Run on the thread pool (NOT the current thread): OnStartup runs under WPF's
                // DispatcherSynchronizationContext, so blocking here with GetResult() while the
                // worker's async continuations post back to this same (blocked) dispatcher thread
                // would deadlock — leaving the parent stuck at "Requesting Administrator privileges".
                // Task.Run gives the worker a thread-pool context with no dispatcher to deadlock on.
                System.Threading.Tasks.Task.Run(() =>
                        BootImageGenerationService.RunElevatedWorkerAsync(e.Args[1], e.Args[2], e.Args[3], loggerFactory))
                    .GetAwaiter().GetResult();
                Shutdown(0);
                return;
            }

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

            // A dedicated HttpClient for large boot-image downloads (no Operator API base address).
            var downloadHttpClient = new HttpClient();

            var services = new AppServices(
                authService,
                operatorApiClient,
                genService,
                new UsbSafetyValidationService(loggerFactory.CreateLogger<UsbSafetyValidationService>()),
                new BootImageDownloadService(downloadHttpClient, loggerFactory.CreateLogger<BootImageDownloadService>()),
                new UsbPartitionProvisioningService(loggerFactory.CreateLogger<UsbPartitionProvisioningService>()),
                new BootImageDeploymentService(loggerFactory.CreateLogger<BootImageDeploymentService>()),
                loggerFactory);

            var mainWindow = new MainWindow();
            mainWindow.NavigateTo(BuildSignInView(mainWindow, services));
            mainWindow.Show();

#if DEV_SIMULATION
            // DEV-ONLY: floating navigator to step through every view without a real
            // Entra ID sign-in. Compiled only in Debug (DEV_SIMULATION); never shipped.
            ShowDevSimulationLauncher(mainWindow, services);
#endif
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
#if DEV_SIMULATION
    /// <summary>
    /// DEV-ONLY: shows the simulation launcher that lets a developer jump directly to any
    /// view and bypass the Entra ID sign-in gate. Compiled only when DEV_SIMULATION is
    /// defined (Debug builds &mdash; see the &lt;DefineConstants&gt; condition in the .csproj), so
    /// it can never appear in a released build.
    ///
    /// MANDATORY: every navigable view MUST be reachable here. When a new view is added to
    /// the app, add an onXxxView action below (and a NavButton in DevSimulationLauncher) in
    /// the SAME change so the navigator always covers every view.
    /// </summary>
    private static void ShowDevSimulationLauncher(MainWindow window, AppServices svc)
    {
        var launcher = new DevMode.DevSimulationLauncher(
            window,
            onSignInView:             () => window.NavigateTo(BuildSignInView(window, svc)),
            onOperationSelectionView: () => window.NavigateTo(BuildOperationSelectionView(window, svc)),
            onGenerateBootImageView:  () => window.NavigateTo(BuildGenerateBootImageView(window, svc)),
            onPrepareUsbView:         () => window.NavigateTo(BuildPrepareStorageDeviceView(window, svc)));
        launcher.Show();
    }
#endif
    private static SignInView BuildSignInView(MainWindow window, AppServices svc)
    {
        var view = new SignInView();
        view.DataContext = new SignInViewModel(
            svc.Auth,
            () => window.NavigateTo(BuildOperationSelectionView(window, svc)));
        return view;
    }

    private static OperationSelectionView BuildOperationSelectionView(MainWindow window, AppServices svc)
    {
        var view = new OperationSelectionView();
        view.DataContext = new OperationSelectionViewModel(op =>
        {
            if (op == "GenerateBootImage")
                window.NavigateTo(BuildGenerateBootImageView(window, svc));
            else if (op == "PrepareUSB")
                window.NavigateTo(BuildPrepareStorageDeviceView(window, svc));
        });
        return view;
    }

    private static GenerateBootImageView BuildGenerateBootImageView(MainWindow window, AppServices svc)
    {
        var view = new GenerateBootImageView();
        view.DataContext = new GenerateBootImageViewModel(
            svc.Gen,
            svc.Auth,
            () => window.NavigateTo(BuildOperationSelectionView(window, svc)));
        return view;
    }

    private static PrepareStorageDeviceView BuildPrepareStorageDeviceView(MainWindow window, AppServices svc)
    {
        var view = new PrepareStorageDeviceView();
        view.DataContext = new PrepareStorageDeviceViewModel(
            svc.OperatorApi,
            svc.Auth,
            svc.UsbValidator,
            svc.Downloader,
            svc.Provisioner,
            svc.Deployer,
            () => window.NavigateTo(BuildOperationSelectionView(window, svc)));
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

    /// <summary>Shared services threaded through the navigation factories.</summary>
    private sealed record AppServices(
        EntraAuthenticationService Auth,
        OperatorApiClient OperatorApi,
        BootImageGenerationService Gen,
        UsbSafetyValidationService UsbValidator,
        BootImageDownloadService Downloader,
        UsbPartitionProvisioningService Provisioner,
        BootImageDeploymentService Deployer,
        ILoggerFactory LoggerFactory);
}
