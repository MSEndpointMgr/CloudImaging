using CloudImaging.OperatorApi.Middleware;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Identity.Web;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        // Middleware pipeline:
        // 1. ProblemDetails (outermost)
        // 2. Entra token validation
        // 3. App-role authorization
        builder.UseMiddleware<ProblemDetailsMiddleware>();
        builder.UseMiddleware<EntraAuthMiddleware>();
        builder.UseMiddleware<AppRoleAuthorizationMiddleware>();
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Imaging Core API typed HTTP client over Private Link (FR-064)
        services.AddHttpClient<ImagingCoreClient>(client =>
        {
            var baseUrl = ctx.Configuration["ImagingCoreApi:BaseUrl"]
                ?? ctx.Configuration["ImagingCoreApi__BaseUrl"]
                ?? throw new InvalidOperationException("ImagingCoreApi__BaseUrl is not configured.");
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        // Table Storage
        services.AddSingleton(sp =>
        {
            var accountName = ctx.Configuration["AzureWebJobsStorage:accountName"]
                ?? ctx.Configuration["AzureWebJobsStorage__accountName"]
                ?? throw new InvalidOperationException("Storage account not configured.");
            return new Azure.Data.Tables.TableServiceClient(
                new Uri($"https://{accountName}.table.core.windows.net"),
                new Azure.Identity.DefaultAzureCredential());
        });

        // Entra ID JWT authentication — validates tokens for OperatorApi (FR-040)
        // Identity.Web 4.x uses AddAuthentication().AddMicrosoftIdentityWebApi() pattern
        services.AddAuthentication()
            .AddMicrosoftIdentityWebApi(ctx.Configuration, "Entra");
    })
    .Build();

host.Run();
