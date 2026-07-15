using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        // Middleware pipeline — order matters:
        // 1. ProblemDetails (outermost — catches all exceptions)
        // 2. Token validation (rejects unauthenticated before rate limiting)
        // 3. Rate limiting (per-session sliding window)
        builder.UseMiddleware<ProblemDetailsMiddleware>();
        builder.UseMiddleware<DeviceSessionTokenValidationMiddleware>();
        builder.UseMiddleware<RateLimitingMiddleware>();
    })
    .ConfigureServices((ctx, services) =>
    {
        // Application Insights — connection string injected via app settings (FR-065, FR-067)
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Imaging Core API typed HTTP client over Private Link (FR-013)
        // Credentials: managed identity via DefaultAzureCredential
        services.AddHttpClient<ImagingCoreClient>(client =>
        {
            var baseUrl = ctx.Configuration["ImagingCoreApi__BaseUrl"]
                ?? throw new InvalidOperationException("ImagingCoreApi__BaseUrl is not configured.");
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        // Table Storage for thumbprint cache (added in T163)
        services.AddSingleton(sp =>
        {
            var connStr = ctx.Configuration["AzureWebJobsStorage__accountName"]
                ?? throw new InvalidOperationException("Storage account not configured.");
            return new Azure.Data.Tables.TableServiceClient(
                new Uri($"https://{connStr}.table.core.windows.net"),
                new Azure.Identity.DefaultAzureCredential());
        });
    })
    .Build();

host.Run();
