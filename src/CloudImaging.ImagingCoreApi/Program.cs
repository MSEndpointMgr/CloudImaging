using CloudImaging.ImagingCoreApi.Middleware;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Storage;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Local variable for Graph scopes: allocated once, satisfies CA1861
string[] graphScopes = ["https://graph.microsoft.com/.default"];

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        // Only ProblemDetails middleware at this layer:
        // ImagingCoreApi is accessible only via Private Link from trusted callers (FR-020).
        builder.UseMiddleware<ProblemDetailsMiddleware>();
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Table Storage: all repositories share one TableServiceClient
        services.AddSingleton(sp =>
        {
            var accountName = ctx.Configuration["AzureWebJobsStorage:accountName"]
                ?? ctx.Configuration["AzureWebJobsStorage__accountName"]
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
        services.AddSingleton<PartitioningSchemeRepository>();
        services.AddSingleton<RecoveryImageRepository>();
        services.AddSingleton<BootMediaCertificateRepository>();
        services.AddSingleton<SessionHistoryRepository>();
        services.AddSingleton<PreFlightOverrideRepository>();
        services.AddSingleton<LocationRepository>();
        services.AddSingleton<RegionConfigurationRepository>();
        services.AddSingleton<WifiNetworkRepository>();
        services.AddSingleton<UserLocationPreferenceRepository>();
        services.AddSingleton<UploadJobRepository>();
        services.AddSingleton<AutopilotRegistrationRepository>();
        services.AddSingleton<AutopilotGroupTagRepository>();
        services.AddSingleton<SetupProfileRepository>();
        services.AddSingleton<SetupProfileTargetRepository>();
        services.AddSingleton(sp => new SetupProfileVersionRepository(
            sp.GetRequiredService<Azure.Data.Tables.TableServiceClient>(),
            sp.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>().GetBlobContainerClient(SetupProfileVersionRepository.ContainerName)));
        services.AddSingleton(sp => new SetupProfileContentRepository(
            sp.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>().GetBlobContainerClient(SetupProfileVersionRepository.ContainerName)));
        services.AddSingleton<SetupProfileHistoryRepository>();
        services.AddSingleton<ImagingStepItemRepository>();
        services.AddSingleton<DriverStore>();
        services.AddSingleton<DriverModelRepository>();
        services.AddSingleton<DriverSetPointerRepository>();
        services.AddSingleton<DriverSyncRunRepository>();
        services.AddSingleton(sp => new DriverSetDocumentRepository(
            sp.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>().GetBlobContainerClient(DriverTables.Container)));

        // The driver-work queue lives on the AzureWebJobsStorage account, where Core's identity
        // already holds Storage Queue Data Contributor; the queue trigger reads Base64 bodies.
        services.AddSingleton(sp =>
        {
            var accountName = ctx.Configuration["AzureWebJobsStorage:accountName"]
                ?? ctx.Configuration["AzureWebJobsStorage__accountName"]
                ?? throw new InvalidOperationException("Storage account name is not configured.");
            return new Azure.Storage.Queues.QueueServiceClient(
                new Uri($"https://{accountName}.queue.core.windows.net"),
                new Azure.Identity.DefaultAzureCredential(),
                new Azure.Storage.Queues.QueueClientOptions { MessageEncoding = Azure.Storage.Queues.QueueMessageEncoding.Base64 });
        });
        services.AddSingleton<DriverWorkSender>();
        services.AddSingleton<IDriverWorkSender>(sp => sp.GetRequiredService<DriverWorkSender>());
        services.AddSingleton<IDriverVendorAdapter, HpDriverVendorAdapter>();
        services.AddSingleton<IDriverVendorAdapter, DellDriverVendorAdapter>();

        // Redirects are followed by DriverStorageService itself so every hop passes the vendor host allowlist.
        services.AddHttpClient(DriverStorageService.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(55))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton(sp =>
        {
            var blobs = sp.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>();
            return new DriverStorageService(
                blobs,
                blobs.GetBlobContainerClient(DriverTables.Container),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(DriverStorageService.HttpClientName),
                sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton(sp => new DriverCatalogService(
            sp.GetRequiredService<DriverStore>(),
            sp.GetRequiredService<DriverModelRepository>(),
            sp.GetRequiredService<DriverStorageService>(),
            sp.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>().GetBlobContainerClient(DriverTables.Container),
            sp.GetServices<IDriverVendorAdapter>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<DriverSyncService>();
        services.AddSingleton<DriverModelService>();
        services.AddSingleton<DriverPolicyService>();
        services.AddSingleton<DriverSyncScheduler>();
        services.AddSingleton<DriverLifecycleService>();
        services.AddSingleton<DriverComponentHistoryService>();
        services.AddSingleton<DriverCustomComponentService>();
        services.AddSingleton<DriverSetResolver>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SetupProfileResolver>();
        services.AddSingleton<SetupProfileService>();

        // Key Vault certificate service (FR-068)
        services.AddSingleton<KeyVaultCertificateService>();
        services.AddSingleton<WifiPasswordStore>();
        services.AddSingleton<WifiNetworkCatalog>();

        // Device pre-flight authorization (FR-026): Microsoft Graph via managed identity
        // Scopes are read-only at startup: use a static field to avoid CA1861
        services.AddSingleton<Azure.Core.TokenCredential>(sp =>
        {
            var managedIdentityClientId = ctx.Configuration["AZURE_CLIENT_ID"];
            return string.IsNullOrWhiteSpace(managedIdentityClientId)
                ? new Azure.Identity.DefaultAzureCredential()
                : new Azure.Identity.ManagedIdentityCredential(
                    Azure.Identity.ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));
        });
        services.AddSingleton(sp =>
        {
            var credential = sp.GetRequiredService<Azure.Core.TokenCredential>();
            return new Microsoft.Graph.GraphServiceClient(
                credential,
                graphScopes);
        });
        services.AddHttpClient<CorporateIdentifierGraphClient>(client =>
            client.BaseAddress = new Uri("https://graph.microsoft.com/"));
        services.AddSingleton<DevicePreFlightAuthorizationService>();
        services.AddSingleton<BulkAssignmentService>();
        services.AddSingleton<ImageDeletionGuardService>();
        services.AddSingleton<BootImageValidationService>();
        services.AddSingleton<IsoExtractionService>();
        services.AddSingleton<UploadPublishService>();
        services.AddSingleton<DeviceSessionLifecycleService>();

        // Autopilot hash registration: needs DeviceManagementServiceConfig.ReadWrite.All on the managed identity to import
        services.AddHttpClient<AutopilotGraphClient>(client =>
            client.BaseAddress = new Uri("https://graph.microsoft.com/"));
        services.AddSingleton<AutopilotRegistrationService>();

        // Azure Blob Storage (SAS token URL generation, FR-025)
        services.AddSingleton(sp =>
        {
            var accountName = ctx.Configuration["AzureWebJobsStorage:accountName"]
                ?? ctx.Configuration["AzureWebJobsStorage__accountName"]
                ?? throw new InvalidOperationException("Storage account name is not configured.");
            return new Azure.Storage.Blobs.BlobServiceClient(
                new Uri($"https://{accountName}.blob.core.windows.net"),
                new Azure.Identity.DefaultAzureCredential());
        });

        // Azure Key Vault (boot media certificate PFX, FR-068)
        services.AddSingleton(sp =>
        {
            var kvUri = ctx.Configuration["KeyVault:VaultUri"]
                ?? ctx.Configuration["KeyVault__VaultUri"]
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
    await sp.GetRequiredService<PartitioningSchemeRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<RecoveryImageRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<BootMediaCertificateRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<SessionHistoryRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<PreFlightOverrideRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<LocationRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<RegionConfigurationRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<WifiNetworkRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<UserLocationPreferenceRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<UploadJobRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<AutopilotRegistrationRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<AutopilotGroupTagRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<SetupProfileRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<SetupProfileTargetRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<SetupProfileVersionRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<SetupProfileHistoryRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<ImagingStepItemRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<DriverStore>().EnsureTablesExistAsync(ct);
    await sp.GetRequiredService<DriverModelRepository>().EnsureTablesExistAsync(ct);
    await sp.GetRequiredService<DriverSetPointerRepository>().EnsureTableExistsAsync(ct);
    await sp.GetRequiredService<DriverSyncRunRepository>().EnsureTablesExistAsync(ct);
    await sp.GetRequiredService<DriverWorkSender>().EnsureQueuesExistAsync(ct);

    // A lost ScheduledSync message never stops the schedule: every start enqueues the next slot,
    // and the copies that lose the slot claim are dropped.
    await sp.GetRequiredService<DriverSyncScheduler>().EnqueueNextAsync(ct);

    // Containers were created only by the IaC, so an environment deployed before one was added
    // failed every write to it until the next template run. Created here for the same reason the
    // tables above are.
    var blobs = sp.GetRequiredService<Azure.Storage.Blobs.BlobServiceClient>();
    foreach (var container in StorageContainers.All)
    {
        await blobs.GetBlobContainerClient(container).CreateIfNotExistsAsync(cancellationToken: ct);
    }
}

host.Run();
