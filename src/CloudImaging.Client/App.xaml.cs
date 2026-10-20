using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using CloudImaging.Client.Logging;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using CloudImaging.Client.Views;
using Microsoft.Extensions.Logging;
#if DEV_SIMULATION
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
#endif

namespace CloudImaging.Client;

public partial class App : Application
{
    // Covers the 20-second network wait plus the 15-second gateway probe.
    private static readonly TimeSpan StartupChecksTimeout = TimeSpan.FromSeconds(40);

    // A Windows RE image may also scan and try each saved Wi-Fi network before the probe.
    private static readonly TimeSpan WifiStartupChecksTimeout = TimeSpan.FromMinutes(2);

    // Long enough that Getting ready reads as a step rather than a flicker when there is nothing to load.
    private static readonly TimeSpan StartupMinimumDisplay = TimeSpan.FromSeconds(1);

    private Serilog.Core.Logger? _logger;

    /// <summary>What the startup checks found, reused so returning to Operation Selection never re-checks on screen.</summary>
    private static StartupReadiness? s_readiness;

    /// <summary>Re-runs the startup checks from Operation Selection's Check again.</summary>
    private static Action? s_recheck;

    /// <summary>The portal's Wi-Fi networks; null on a Windows PE image, which has no Wi-Fi.</summary>
    private static ManagedWifiService? s_managedWifi;

    /// <summary>Wires up fatal-exception reporting, configures logging, and shows the first view.</summary>
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

            // Decided from the image itself, so no setting can disagree with what the image can do.
            if (BootImageCapabilities.SupportsWifi())
            {
                s_managedWifi = new ManagedWifiService(
                    startupResult.Certificate,
                    new WirelessConnectionService(loggerFactory.CreateLogger<WirelessConnectionService>()),
                    loggerFactory.CreateLogger<ManagedWifiService>());
                s_managedWifi.LoadFromMedia();
            }

            var mainWindow = new MainWindow();

            // Navigate before Show: a page arriving while MainWindow.Loaded swaps the theme dictionary loses its inherited text colour.
            _ = ShowOperationSelectionAsync(mainWindow, gatewayClient, loggerFactory, rememberReadiness: true);
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

    /// <summary>Flushes and disposes the Serilog logger before the process exits.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Dispose();
        base.OnExit(e);
    }

    /// <summary>View Log in the shared footer (<c>ClientFooterStyle</c> in App.xaml).</summary>
    private void FooterViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        DialogHost.ShowDimmed(new LogViewerWindow(), Window.GetWindow((DependencyObject)sender));
    }

    // ── Navigation factories ──────────────────────────────────────────────────

    /// <summary>
    /// Shows Getting ready while it checks the prerequisites every operation depends on (local
    /// disk, network, Device Gateway, and the portal's Autopilot setting when this boot image can
    /// register), then opens Operation Selection in its final state so no card visibly switches
    /// from disabled to enabled.
    /// </summary>
    private static async Task ShowOperationSelectionAsync(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf,
        AutopilotHashCaptureService? autopilotCapture = null,
        TimeSpan? autopilotPollInterval = null,
        bool rememberReadiness = false,
        StartupViewModel? startupChecks = null)
    {
        autopilotCapture ??= new AutopilotHashCaptureService(lf.CreateLogger<AutopilotHashCaptureService>());
        startupChecks ??= new StartupViewModel(gateway.ProbeAsync, autopilotCapture.IsToolingPresent, logger: lf.CreateLogger<StartupViewModel>(),
            managedWifi: s_managedWifi, wifiSupported: s_managedWifi is not null);
        window.NavigateTo(new StartupView { DataContext = startupChecks });
        var minimumDisplay = Task.Delay(StartupMinimumDisplay);

        using var timeout = new CancellationTokenSource(s_managedWifi is null ? StartupChecksTimeout : WifiStartupChecksTimeout);
        var readiness = await startupChecks.RunAsync(timeout.Token);

        // Once online, fetch the current networks so a password changed in the portal reaches the USB copy.
        if (readiness.CanReachService && s_managedWifi is not null)
            _ = s_managedWifi.RefreshFromGatewayAsync(gateway);
        Action recheck = () => _ = ShowOperationSelectionAsync(window, gateway, lf, autopilotCapture, autopilotPollInterval, rememberReadiness);
        if (rememberReadiness)
        {
            s_readiness = readiness;
            s_recheck = recheck;
        }

        await minimumDisplay;
        window.NavigateTo(BuildOperationSelectionView(window, gateway, lf,
            autopilotCapture: autopilotCapture, autopilotPollInterval: autopilotPollInterval, readiness: readiness, recheck: recheck));
    }

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
            onResultsNotAuthorizedView: () => window.NavigateTo(BuildBlockedResultsView(window, gateway, lf, new SessionStatusResponse
            {
                SessionId = Guid.NewGuid(),
                State = "SessionNotAuthorized",
                PreFlightChecks =
                [
                    new PreFlightCheckResult { Check = PreFlightCheck.AutopilotPresence, Outcome = PreFlightCheckOutcome.Passed, Observed = PreFlightObserved.Autopilot },
                    new PreFlightCheckResult { Check = PreFlightCheck.FirmwareMode, Outcome = PreFlightCheckOutcome.Passed, Observed = nameof(FirmwareMode.Uefi) },
                    new PreFlightCheckResult { Check = PreFlightCheck.SecureBoot, Outcome = PreFlightCheckOutcome.Failed, Observed = nameof(SecureBootState.Disabled) },
                    new PreFlightCheckResult { Check = PreFlightCheck.TpmVersion, Outcome = PreFlightCheckOutcome.NotRequired, Observed = nameof(TpmPresence.Tpm20) },
                ],
            }, "DEV-SIM-0001")),
            autopilotViews:
            [
                ("7 · Autopilot: startup and card on Operation Selection", () => _ = ShowOperationSelectionAsync(window,
                    DevMode.DevAutopilotSimulation.Gateway(["PendingApproval", "Importing", "Imported"]), lf,
                    autopilotCapture: DevMode.DevAutopilotSimulation.Capture(withTpmData: true), autopilotPollInterval: DevMode.DevAutopilotSimulation.PollInterval)),
                ("8 · Autopilot: approved and imported", () => window.NavigateTo(BuildAutopilotRegistrationView(window,
                    DevMode.DevAutopilotSimulation.Gateway(["PendingApproval", "Importing", "Imported"]), lf,
                    DevMode.DevAutopilotSimulation.Capture(withTpmData: true), DevMode.DevAutopilotSimulation.PollInterval, DevMode.DevAutopilotSimulation.Readiness))),
                ("9 · Autopilot: hash without TPM data", () => window.NavigateTo(BuildAutopilotRegistrationView(window,
                    DevMode.DevAutopilotSimulation.Gateway(["PendingApproval", "Importing", "Imported"]), lf,
                    DevMode.DevAutopilotSimulation.Capture(withTpmData: false), DevMode.DevAutopilotSimulation.PollInterval, DevMode.DevAutopilotSimulation.Readiness))),
                ("10 · Autopilot: rejected", () => window.NavigateTo(BuildAutopilotRegistrationView(window,
                    DevMode.DevAutopilotSimulation.Gateway(["PendingApproval", "Rejected"], rejectionReason: "Not a company-owned device."), lf,
                    DevMode.DevAutopilotSimulation.Capture(withTpmData: true), DevMode.DevAutopilotSimulation.PollInterval, DevMode.DevAutopilotSimulation.Readiness))),
                ("11 · Autopilot: submission refused", () => window.NavigateTo(BuildAutopilotRegistrationView(window,
                    DevMode.DevAutopilotSimulation.Gateway(["PendingApproval"], refusal: "Autopilot registration is turned off in the portal."), lf,
                    DevMode.DevAutopilotSimulation.Capture(withTpmData: true), DevMode.DevAutopilotSimulation.PollInterval, DevMode.DevAutopilotSimulation.Readiness))),
                ("12 · Startup: no disk, no network", () => _ = ShowOperationSelectionAsync(window, gateway, lf,
                    startupChecks: new StartupViewModel(
                        _ => Task.FromResult(new GatewayProbeResult(GatewayReachability.Unreachable, null)),
                        autopilotToolingPresent: false,
                        findLocalDisks: () => [],
                        findNetworkAdapter: () => null,
                        networkWait: TimeSpan.FromSeconds(3)))),
            ]);
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
        ILoggerFactory lf,
        bool startImagingImmediately = false,
        AutopilotHashCaptureService? autopilotCapture = null,
        TimeSpan? autopilotPollInterval = null,
        StartupReadiness? readiness = null,
        Action? recheck = null)
    {
        var config = LoadConfiguration();
        var view = new OperationSelectionView();
        autopilotCapture ??= new AutopilotHashCaptureService(lf.CreateLogger<AutopilotHashCaptureService>());
        readiness ??= s_readiness;
        recheck ??= s_recheck;
        var autopilotAvailability = readiness?.AutopilotAvailability;
        view.DataContext = new OperationSelectionViewModel(
            gateway,
            (sessionResponse, serialNumber) =>
                window.NavigateTo(BuildSessionInitView(window, gateway, lf, sessionResponse, serialNumber)),
            new SystemClockSynchronizationService(lf.CreateLogger<SystemClockSynchronizationService>()),
#if DEV_SIMULATION
            // Always shown in dev so its placement can be checked against every Operation Selection state.
            commandPromptEnabled: true,
#else
            commandPromptEnabled: config.CommandPromptEnabled ?? false,
#endif
            setMainWindowTopmost: window.SetTopmost,
            commandPromptLauncher: new CommandPromptLauncherService(lf.CreateLogger<CommandPromptLauncherService>()),
            autopilotToolingPresent: autopilotCapture.IsToolingPresent,
            navigateToAutopilot: () => window.NavigateTo(BuildAutopilotRegistrationView(window, gateway, lf, autopilotCapture, autopilotPollInterval, readiness, recheck)),
            postureDetector: new DevicePostureDetector(lf.CreateLogger<DevicePostureDetector>()),
            startImagingImmediately: startImagingImmediately,
            autopilotAvailability: autopilotAvailability,
            readiness: readiness,
            recheck: recheck,
            managedWifi: s_managedWifi,
            logger: lf.CreateLogger<OperationSelectionViewModel>());
        return view;
    }

    private static AutopilotRegistrationView BuildAutopilotRegistrationView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf,
        AutopilotHashCaptureService capture,
        TimeSpan? pollInterval = null,
        StartupReadiness? readiness = null,
        Action? recheck = null)
    {
        var viewModel = new AutopilotRegistrationViewModel(
            gateway,
            capture,
            navigateBack: () => window.NavigateTo(BuildOperationSelectionView(window, gateway, lf, autopilotCapture: capture, autopilotPollInterval: pollInterval, readiness: readiness, recheck: recheck)),
            new SystemClockSynchronizationService(lf.CreateLogger<SystemClockSynchronizationService>()),
            logger: lf.CreateLogger<AutopilotRegistrationViewModel>(),
            pollInterval: pollInterval);
        var view = new AutopilotRegistrationView { DataContext = viewModel };
        view.Unloaded += (_, _) => viewModel.Dispose();
        viewModel.Start();
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
        var viewModel = new SessionInitViewModel(
            gateway,
            session.SessionId,
            session.Passcode,
            deviceSerialNumber,
            navigateToResults: (outcome, serialNumber, errorDetail, supportReferenceCode) =>
                window.NavigateTo(BuildResultsView(window, gateway, lf, outcome, serialNumber, errorDetail, supportReferenceCode)),
            navigateToProgress: (status, serialNumber) =>
                window.NavigateTo(BuildProgressView(window, gateway, lf, session.SessionId, serialNumber, status)),
            navigateToBlocked: (status, serialNumber) =>
                window.NavigateTo(BuildBlockedResultsView(window, gateway, lf, status, serialNumber)),
            navigateToStart: () =>
                window.NavigateTo(BuildOperationSelectionView(window, gateway, lf)),
            logger: lf.CreateLogger<SessionInitViewModel>());
        view.DataContext = viewModel;

        // Without this the poll loop outlives the page and keeps touching the abandoned session,
        // which holds it alive server-side and prevents it from ever expiring.
        view.Unloaded += (_, _) => viewModel.Dispose();
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

    /// <summary>Blocked outcome: lists the pre-flight checks and lets the device start a new session in place.</summary>
    private static ResultsView BuildBlockedResultsView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf,
        SessionStatusResponse status,
        string? serialNumber)
    {
        var view = new ResultsView();
        view.DataContext = new ResultsViewModel(
            ResultsViewModel.Outcome.NotAuthorized,
            serialNumber,
            null,
            () => window.NavigateTo(BuildOperationSelectionView(window, gateway, lf)),
            sessionId: status.SessionId,
            preFlightChecks: status.PreFlightChecks,
            tryAgain: () => window.NavigateTo(BuildOperationSelectionView(window, gateway, lf, startImagingImmediately: true)),
            logger: lf.CreateLogger<ResultsViewModel>());
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
