using CloudImaging.OperatorApi.Middleware;
using CloudImaging.OperatorApi.Security;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
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

            // The default HttpClient.Timeout (100s) is shorter than the portal server's own
            // timeout for boot/recovery/OS image "publish" calls (see portal server's
            // PUBLISH_TIMEOUT_MS), which download the whole staged blob to verify its SHA-256
            // hash and copy it to its published path — this scales with image size and can
            // legitimately take well over 100s for multi-GB OS images (5-10 GB typical). Leaving
            // the default here meant this hop timed out and threw an unhandled exception BEFORE
            // the portal server's own timeout could ever be reached, surfacing as a generic 500
            // on every sufficiently large publish. Keep this comfortably below the portal
            // server's timeout so, if a publish genuinely never completes, this hop is the one
            // that reports it (rather than the portal server timing out first and masking it).
            client.Timeout = TimeSpan.FromSeconds(280);
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

        // Full bearer-token validator (signature/issuer/audience/lifetime) used by
        // EntraAuthMiddleware in the isolated-worker pipeline (FR-061).
        services.AddSingleton(_ =>
        {
            var tenantId = ctx.Configuration["Entra:TenantId"]
                ?? ctx.Configuration["Entra__TenantId"]
                ?? throw new InvalidOperationException("Entra__TenantId is not configured.");
            var operatorApiClientId = ctx.Configuration["Entra:ClientId"]
                ?? ctx.Configuration["Entra__ClientId"]
                ?? throw new InvalidOperationException("Entra__ClientId is not configured.");
            var instance = ctx.Configuration["Entra:Instance"]
                ?? ctx.Configuration["Entra__Instance"];

            return new EntraTokenValidator(new EntraValidationOptions
            {
                TenantId = tenantId,
                Instance = instance,
                ValidAudiences = [operatorApiClientId],
            });
        });
    })
    .Build();

host.Run();
