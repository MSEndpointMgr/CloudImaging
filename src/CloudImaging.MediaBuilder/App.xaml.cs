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

        base.OnStartup(e);
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
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(settingsPath))
            return new MediaBuilderConfig(string.Empty, string.Empty, string.Empty, string.Empty);

        try
        {
            using var stream = File.OpenRead(settingsPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var entraId = root.GetProperty("EntraId");
            var baseUrl = root.TryGetProperty("OperatorApi", out var oa)
                ? oa.GetProperty("BaseUrl").GetString() ?? string.Empty
                : string.Empty;

            return new MediaBuilderConfig(
                entraId.GetProperty("ClientId").GetString() ?? string.Empty,
                entraId.GetProperty("TenantId").GetString() ?? string.Empty,
                entraId.GetProperty("OperatorApiScope").GetString() ?? string.Empty,
                baseUrl);
        }
        catch
        {
            return new MediaBuilderConfig(string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    private sealed record MediaBuilderConfig(
        string ClientId,
        string TenantId,
        string OperatorApiScope,
        string OperatorApiBaseUrl);
}
