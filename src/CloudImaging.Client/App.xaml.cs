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
        _logger = LoggingConfiguration.CreateLogger();
        Serilog.Log.Logger = _logger;

        var loggerFactory = LoggingConfiguration.CreateLoggerFactory(_logger);
        var config = LoadConfiguration();

        // Prepare mTLS-enabled HttpClient for Device Gateway API (FR-071)
        var handler = new HttpClientHandler();
        var coordinator = new SessionStartupCoordinator(
            loggerFactory.CreateLogger<SessionStartupCoordinator>());
        coordinator.ConfigureMtlsCertificate(handler);

        var httpClient = new HttpClient(handler);
        if (!string.IsNullOrEmpty(config.DeviceGatewayBaseUrl))
            httpClient.BaseAddress = new Uri(config.DeviceGatewayBaseUrl);

        var gatewayClient = new DeviceGatewayApiClient(httpClient);

        var mainWindow = new MainWindow();
        mainWindow.NavigateTo(BuildOperationSelectionView(mainWindow, gatewayClient, loggerFactory));
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Dispose();
        base.OnExit(e);
    }

    // ── Navigation factories ──────────────────────────────────────────────────

    private static OperationSelectionView BuildOperationSelectionView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf)
    {
        var view = new OperationSelectionView();
        view.DataContext = new OperationSelectionViewModel(
            gateway,
            sessionResponse =>
            {
                if (sessionResponse is CreateSessionResponse resp)
                    window.NavigateTo(BuildSessionInitView(window, gateway, lf, resp));
            });
        return view;
    }

    private static SessionInitView BuildSessionInitView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf,
        CreateSessionResponse session)
    {
        var view = new SessionInitView();
        view.DataContext = new SessionInitViewModel(
            gateway,
            session.SessionId,
            session.Passcode,
            (outcome, serialNumber, errorDetail) =>
                window.NavigateTo(BuildResultsView(window, gateway, lf, outcome, serialNumber, errorDetail)));
        return view;
    }

    private static ResultsView BuildResultsView(
        MainWindow window,
        DeviceGatewayApiClient gateway,
        ILoggerFactory lf,
        ResultsViewModel.Outcome outcome,
        string? serialNumber,
        string? errorDetail)
    {
        var view = new ResultsView();
        view.DataContext = new ResultsViewModel(
            outcome,
            serialNumber,
            errorDetail,
            () => window.NavigateTo(BuildOperationSelectionView(window, gateway, lf)));
        return view;
    }

    // ── Configuration ─────────────────────────────────────────────────────────

    private static ClientConfig LoadConfiguration()
    {
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(settingsPath))
            return new ClientConfig(string.Empty);

        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var doc = JsonDocument.Parse(stream);
            var baseUrl = doc.RootElement
                .TryGetProperty("DeviceGatewayApi", out var gw)
                    ? gw.GetProperty("BaseUrl").GetString() ?? string.Empty
                    : string.Empty;
            return new ClientConfig(baseUrl);
        }
        catch
        {
            return new ClientConfig(string.Empty);
        }
    }

    private sealed record ClientConfig(string DeviceGatewayBaseUrl);
}
