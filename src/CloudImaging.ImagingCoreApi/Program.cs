using CloudImaging.ImagingCoreApi.Middleware;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        // Only ProblemDetails middleware at this layer —
        // ImagingCoreApi is accessible only via Private Link from trusted callers (FR-020).
        builder.UseMiddleware<ProblemDetailsMiddleware>();
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Table Storage — all repositories share one TableServiceClient
        services.AddSingleton(sp =>
        {
            var accountName = ctx.Configuration["AzureWebJobsStorage__accountName"]
                ?? throw new InvalidOperationException("Storage account name is not configured.");
            return new Azure.Data.Tables.TableServiceClient(
                new Uri($"https://{accountName}.table.core.windows.net"),
                new Azure.Identity.DefaultAzureCredential());
        });

        // Repositories
        services.AddSingleton<DeviceSessionRepository>();
        services.AddSingleton<ImagingStepRepository>();
        services.AddSingleton<OsImageRepository>();
        services.AddSingleton<BootImageRepository>();
        services.AddSingleton<BrandingRepository>();
        services.AddSingleton<PortalConfigurationRepository>();
        services.AddSingleton<BootMediaCertificateRepository>();

        // Key Vault certificate service (FR-068)
        services.AddSingleton<KeyVaultCertificateService>();

        // Azure Blob Storage (SAS token URL generation, FR-025)
        services.AddSingleton(sp =>
        {
            var accountName = ctx.Configuration["AzureWebJobsStorage__accountName"]
                ?? throw new InvalidOperationException("Storage account name is not configured.");
            return new Azure.Storage.Blobs.BlobServiceClient(
                new Uri($"https://{accountName}.blob.core.windows.net"),
                new Azure.Identity.DefaultAzureCredential());
        });

        // Azure Key Vault (boot media certificate PFX, FR-068)
        services.AddSingleton(sp =>
        {
            var kvUri = ctx.Configuration["KeyVault__VaultUri"]
                ?? throw new InvalidOperationException("KeyVault__VaultUri is not configured.");
            return new Azure.Security.KeyVault.Secrets.SecretClient(
                new Uri(kvUri),
                new Azure.Identity.DefaultAzureCredential());
        });
    })
    .Build();

// Ensure Table Storage tables exist on startup
await using (var scope = host.Services.CreateAsyncScope())
{
    var sp = scope.ServiceProvider;
    var ct = CancellationToken.None;
    await sp.GetRequiredService<DeviceSessionRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<ImagingStepRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<OsImageRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<BootImageRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<BrandingRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<PortalConfigurationRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<BootMediaCertificateRepository>().EnsureTableExistsAsync(ct);
}

host.Run();
