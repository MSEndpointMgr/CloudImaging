using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using CloudImaging.DeviceGatewayApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication(builder =>
    {
        // Middleware pipeline — order matters:
        // 1. ProblemDetails (outermost — catches all exceptions)
        // 2. mTLS cert validation (client certificate must match active thumbprint)
        // 3. Token validation (rejects unauthenticated before rate limiting)
        // 4. Rate limiting (per-session sliding window)
        builder.UseMiddleware<ProblemDetailsMiddleware>();
        builder.UseMiddleware<MtlsCertificateValidationMiddleware>();
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

        // Table Storage for thumbprint cache (T163)
        services.AddSingleton(sp =>
        {
            var connStr = ctx.Configuration["AzureWebJobsStorage__accountName"]
                ?? throw new InvalidOperationException("Storage account not configured.");
            return new Azure.Data.Tables.TableServiceClient(
                new Uri($"https://{connStr}.table.core.windows.net"),
                new Azure.Identity.DefaultAzureCredential());
        });

        // Device-session token service (issues + validates opaque session bearer tokens)
        services.AddSingleton<CloudImaging.DeviceGatewayApi.Security.DeviceSessionTokenService>();

        // Single-use nonce store for proof-of-possession replay prevention (FR-069).
        // Backed by an atomic Table Storage insert: AddEntity throws HTTP 409 on a duplicate row,
        // which we treat as a replay. The table is created lazily on first use (404 → create + retry).
        services.AddSingleton<DeviceSessionNonceStore>(sp =>
        {
            var tableService = sp.GetRequiredService<Azure.Data.Tables.TableServiceClient>();
            var logger       = sp.GetRequiredService<ILogger<DeviceSessionNonceStore>>();
            var tableClient  = tableService.GetTableClient("DeviceSessionNonce");

            async Task<bool> Register(string nonceKey, DateTimeOffset expiresAt, CancellationToken ct)
            {
                var entity = new Azure.Data.Tables.TableEntity("nonce", nonceKey)
                {
                    ["ExpiresAt"] = expiresAt.UtcDateTime,
                };

                try
                {
                    await tableClient.AddEntityAsync(entity, ct);
                    return true; // first use
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 409)
                {
                    return false; // duplicate row → replay
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    // Table not created yet — create once and retry.
                    await tableClient.CreateIfNotExistsAsync(ct);
                    await tableClient.AddEntityAsync(entity, ct);
                    return true;
                }
            }

            return new DeviceSessionNonceStore(Register, logger);
        });

        // Boot-media certificate thumbprint cache (60s TTL, FR-069)
        // Loader reads the active thumbprint from Table Storage via the internal API client.
        services.AddSingleton<BootMediaCertificateThumbprintCache>(sp =>
        {
            var tableService = sp.GetRequiredService<Azure.Data.Tables.TableServiceClient>();
            var logger       = sp.GetRequiredService<ILogger<BootMediaCertificateThumbprintCache>>();

            var tableClient  = tableService.GetTableClient("BootMediaCertificate");
            async Task<string?> Loader(CancellationToken ct)
            {
                await foreach (var entity in tableClient.QueryAsync<Azure.Data.Tables.TableEntity>(
                    e => e.PartitionKey == "cert" && e.GetBoolean("IsActive") == true,
                    maxPerPage: 1,
                    cancellationToken: ct))
                {
                    return entity.RowKey;
                }
                return null;
            }

            return new BootMediaCertificateThumbprintCache(Loader, logger);
        });

        // Register the mTLS middleware so it can be resolved from DI
        services.AddSingleton<MtlsCertificateValidationMiddleware>();
    })
    .Build();

host.Run();
