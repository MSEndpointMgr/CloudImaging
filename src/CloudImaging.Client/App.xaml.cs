using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using CloudImaging.Client.Logging;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using CloudImaging.Client.Views;
using Microsoft.Extensions.Logging;

namespace CloudImaging.Client;

public partial class App : Application
{
    private Serilog.Core.Logger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Surface any unhandled failure instead of the process dying silently
        // (e.g. under WinPE or when launched elevated). Wire these before any startup work.
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

            var loggerFactory = LoggingConfiguration.CreateLoggerFactory(_logger);
            var config = LoadConfiguration();

            // Prepare mTLS-enabled HttpClient for Device Gateway API (FR-071)
            var handler = new HttpClientHandler();
            var coordinator = new SessionStartupCoordinator(
                loggerFactory.CreateLogger<SessionStartupCoordinator>());
            var startupResult = coordinator.ConfigureMtlsCertificate(handler);

            var httpClient = new HttpClient(handler);
            if (!string.IsNullOrEmpty(config.DeviceGatewayBaseUrl))
                httpClient.BaseAddress = new Uri(config.DeviceGatewayBaseUrl);

            var gatewayClient = new DeviceGatewayApiClient(
                httpClient, startupResult.Certificate, loggerFactory.CreateLogger<DeviceGatewayApiClient>());

            // T071b/FR-059a: kick off the boot image self-update check in the background.
            // Fire-and-forget by design — it only affects the NEXT boot from this USB media
            // (WinPE already loaded the current boot.wim into RAM before the Client started),
            // so it must never delay startup or the current session, and it swallows its own
            // failures internally (see BootImageSelfUpdateService remarks).
            var selfUpdateService = new BootImageSelfUpdateService(
                gatewayClient,
                new HttpClient(),
                loggerFactory.CreateLogger<BootImageSelfUpdateService>());
            _ = selfUpdateService.CheckAndUpdateAsync();

            var mainWindow = new MainWindow();
            mainWindow.NavigateTo(BuildOperationSelectionView(mainWindow, gatewayClient, loggerFactory));
            mainWindow.Show();

#if DEV_SIMULATION
            // DEV-ONLY: floating navigator to step through every view without a real
            // device, boot-media certificate, or reachable Device Gateway API. Compiled
            // only in Debug (DEV_SIMULATION); never shipped.
            ShowDevSimulationLauncher(mainWindow, gatewayClient, loggerFactory);
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
    /// dies without a trace (important under WinPE or when launched elevated).
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
            $"Cloud Imaging Client failed to start.\n\n{ex?.Message}\n\n{ex}",
            "Cloud Imaging Client",
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
    /// view without a real device, boot-media certificate, or reachable Device Gateway API.
    /// Compiled only when DEV_SIMULATION is defined (Debug builds — see the
    /// &lt;DefineConstants&gt; condition in the .csproj), so it can never appear in a
    /// released build.
    /// </summary>
    private static void ShowDevSimulationLauncher(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf)
    {
        var launcher = new DevMode.DevSimulationLauncher(
            window,
            onOperationSelectionView:   () => window.NavigateTo(BuildOperationSelectionView(window, gateway, lf)),
            onSessionInitView:          () => window.NavigateTo(BuildSampleSessionInitView(window, gateway, lf)),
            onProgressView:             () => window.NavigateTo(BuildSampleProgressView()),
            onResultsSuccessView:       () => window.NavigateTo(BuildResultsView(window, gateway, lf, ResultsViewModel.Outcome.Success, "1234-5678-90", null)),
            onResultsFailureView:       () => window.NavigateTo(BuildResultsView(window, gateway, lf, ResultsViewModel.Outcome.Failure, "1234-5678-90", "Disk format failed: no writable target volume found.")),
            onResultsNotAuthorizedView: () => window.NavigateTo(BuildResultsView(window, gateway, lf, ResultsViewModel.Outcome.NotAuthorized, null, null)));
        launcher.Show();
    }

    /// <summary>DEV-ONLY: SessionInitView seeded with a fake session so the screen renders.</summary>
    private static SessionInitView BuildSampleSessionInitView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf) =>
        BuildSessionInitView(window, gateway, lf, new CreateSessionResponse
        {
            SessionId = Guid.NewGuid(),
            Passcode = "428913",
            State = "AwaitingAuthorization",
        }, "DEV-SIM-0001");

    /// <summary>DEV-ONLY: ProgressView seeded with a representative mid-imaging state.</summary>
    private static ProgressView BuildSampleProgressView()
    {
        var vm = new ProgressViewModel();
        vm.StatusMessage = "Preparing the target disk…";
        vm.AppendActivity("Target disk selected for formatting: disk 0.");
        vm.AppendActivity("Disk 0 formatted and assigned drive D:.");
        vm.UpdateStep(CloudImaging.Contracts.Enums.ImagingStepName.FormatDisk, CloudImaging.Contracts.Enums.ImagingStepStatus.Completed);
        vm.UpdateStep(CloudImaging.Contracts.Enums.ImagingStepName.DownloadImage, CloudImaging.Contracts.Enums.ImagingStepStatus.InProgress);
        vm.StatusMessage = "Downloading operating system image…";
        vm.AppendActivity("Starting image download from https://cistoragedev.blob.core.windows.net/images/…");
        vm.OverallPercent = 45;
        return new ProgressView { DataContext = vm };
    }
#endif

    private static OperationSelectionView BuildOperationSelectionView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf)
    {
        var config = LoadConfiguration();
        var view = new OperationSelectionView();
        view.DataContext = new OperationSelectionViewModel(
            gateway,
            (sessionResponse, serialNumber) =>
                window.NavigateTo(BuildSessionInitView(window, gateway, lf, sessionResponse, serialNumber)),
            new SystemClockSynchronizationService(lf.CreateLogger<SystemClockSynchronizationService>()),
            commandPromptEnabled: config.CommandPromptEnabled ?? false,
            setMainWindowTopmost: window.SetTopmost,
            commandPromptLauncher: new CommandPromptLauncherService(lf.CreateLogger<CommandPromptLauncherService>()));
        return view;
    }

    private static SessionInitView BuildSessionInitView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf,
        CreateSessionResponse session,
        string? deviceSerialNumber)
    {
        var view = new SessionInitView();
        view.DataContext = new SessionInitViewModel(
            gateway,
            session.SessionId,
            session.Passcode,
            deviceSerialNumber,
            navigateToResults: (outcome, serialNumber, errorDetail, supportReferenceCode) =>
                window.NavigateTo(BuildResultsView(window, gateway, lf, outcome, serialNumber, errorDetail, supportReferenceCode)),
            navigateToProgress: (status, serialNumber) =>
                window.NavigateTo(BuildProgressView(window, gateway, lf, session.SessionId, serialNumber, status)));
        return view;
    }

    /// <summary>
    /// Builds the ProgressView and starts <see cref="ImagingWorkflowViewModel"/>, which drives the
    /// Format → Download → Apply pipeline and navigates onward to ResultsView on completion/failure.
    /// </summary>
    private static ProgressView BuildProgressView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf,
        Guid sessionId,
        string? deviceSerialNumber,
        SessionStatusResponse initialStatus)
    {
        var progressViewModel = new ProgressViewModel();
        var workflow = new ImagingWorkflowViewModel(
            gateway,
            sessionId,
            deviceSerialNumber,
            initialStatus,
            progressViewModel,
            navigateToResults: (outcome, serialNumber, errorDetail, supportReferenceCode) =>
                window.NavigateTo(BuildResultsView(window, gateway, lf, outcome, serialNumber, errorDetail, supportReferenceCode)),
            lf);
        workflow.Start();
        return new ProgressView { DataContext = progressViewModel };
    }

    private static ResultsView BuildResultsView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf,
        ResultsViewModel.Outcome outcome,
        string? serialNumber,
        string? errorDetail,
        string? explicitSupportReferenceCode = null)
    {
        var view = new ResultsView();
        var restartService = new SystemRestartService(lf.CreateLogger<SystemRestartService>());
        view.DataContext = new ResultsViewModel(
            outcome,
            serialNumber,
            errorDetail,
            () => window.NavigateTo(BuildOperationSelectionView(window, gateway, lf)),
            explicitSupportReferenceCode,
            restartSystem: restartService.Restart);
        return view;
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    private static ClientConfig LoadConfiguration()
    {
        // The committed appsettings.json ships with an empty placeholder so no environment
        // specific values leak into a public release. Developers put a real dev Device
        // Gateway base URL in appsettings.Local.json, which is git-ignored and overlaid
        // here at runtime (mirrors the Media Builder config pattern).
        var config = ReadConfigFile("appsettings.json");
        config = Overlay(config, ReadConfigFile("appsettings.Local.json"));
        return config;
    }

    private static ClientConfig ReadConfigFile(string fileName)
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(settingsPath))
            return new ClientConfig(string.Empty, null);

        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var doc = JsonDocument.Parse(stream);
            var baseUrl = doc.RootElement
                .TryGetProperty("DeviceGatewayApi", out var gw) && gw.TryGetProperty("BaseUrl", out var bu)
                    ? bu.GetString() ?? string.Empty
                    : string.Empty;
            var commandPromptEnabled = doc.RootElement
                .TryGetProperty("SupportTools", out var st) && st.TryGetProperty("CommandPromptEnabled", out var cpe)
                && cpe.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? cpe.GetBoolean()
                    : (bool?)null;
            return new ClientConfig(baseUrl, commandPromptEnabled);
        }
        catch
        {
            return new ClientConfig(string.Empty, null);
        }
    }

    // Non-empty/non-null values from the overlay replace the corresponding base values.
    private static ClientConfig Overlay(ClientConfig baseConfig, ClientConfig overlay) =>
        new(
            string.IsNullOrWhiteSpace(overlay.DeviceGatewayBaseUrl)
                ? baseConfig.DeviceGatewayBaseUrl
                : overlay.DeviceGatewayBaseUrl,
            overlay.CommandPromptEnabled ?? baseConfig.CommandPromptEnabled);

    private sealed record ClientConfig(string DeviceGatewayBaseUrl, bool? CommandPromptEnabled);
}
