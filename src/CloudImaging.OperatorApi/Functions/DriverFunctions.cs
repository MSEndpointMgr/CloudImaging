using System.Net;
using System.Text.RegularExpressions;
using CloudImaging.OperatorApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Driver proxy endpoints (<c>/api/drivers/...</c> to Core <c>/api/internal/drivers/...</c>). All
/// rules live in the Imaging Core API; this layer checks route segments, forwards the body and
/// query unchanged and relays status and body. PortalAccess only.
/// </summary>
public sealed partial class DriverFunctions
{
    private readonly ImagingCoreClient _coreClient;

    /// <param name="coreClient">Imaging Core API client the requests are forwarded to.</param>
    public DriverFunctions(ImagingCoreClient coreClient)
    {
        _coreClient = coreClient;
    }

    /// <summary>True for a route segment Core can receive as-is: vendor, release, lineage key or GUID.</summary>
    public static bool IsSafeSegment(string? segment) => segment is not null && SafeSegment().IsMatch(segment);

    /// <summary>GET drivers/vendors.</summary>
    [Function("GetDriverVendors")]
    public Task<HttpResponseData> GetDriverVendors(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/vendors")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "vendors", context.CancellationToken);

    /// <summary>GET drivers/catalog/models?vendor=&amp;search=.</summary>
    [Function("GetDriverCatalogModels")]
    public Task<HttpResponseData> GetDriverCatalogModels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/catalog/models")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "catalog/models", context.CancellationToken);

    /// <summary>POST drivers/models/preview.</summary>
    [Function("PreviewDriverModels")]
    public Task<HttpResponseData> PreviewDriverModels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/preview")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "models/preview", context.CancellationToken);

    /// <summary>POST drivers/models: onboard as drafts.</summary>
    [Function("OnboardDriverModels")]
    public Task<HttpResponseData> OnboardDriverModels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "models", context.CancellationToken);

    /// <summary>GET drivers/models.</summary>
    [Function("GetDriverModels")]
    public Task<HttpResponseData> GetDriverModels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/models")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "models", context.CancellationToken);

    /// <summary>GET drivers/models/{id}?release=.</summary>
    [Function("GetDriverModel")]
    public Task<HttpResponseData> GetDriverModel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/models/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? ForwardAsync(req, HttpMethod.Get, $"models/{modelId:D}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/models/{id}/complete: Complete onboarding.</summary>
    [Function("CompleteDriverModelOnboarding")]
    public Task<HttpResponseData> CompleteDriverModelOnboarding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/{id}/complete")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? ForwardAsync(req, HttpMethod.Post, $"models/{modelId:D}/complete", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/models/{id}/releases.</summary>
    [Function("AddDriverModelRelease")]
    public Task<HttpResponseData> AddDriverModelRelease(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/{id}/releases")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? ForwardAsync(req, HttpMethod.Post, $"models/{modelId:D}/releases", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/sync/runs: Sync now.</summary>
    [Function("StartDriverSyncRun")]
    public Task<HttpResponseData> StartDriverSyncRun(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/sync/runs")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "sync/runs", context.CancellationToken);

    /// <summary>GET drivers/sync/runs.</summary>
    [Function("GetDriverSyncRuns")]
    public Task<HttpResponseData> GetDriverSyncRuns(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/sync/runs")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "sync/runs", context.CancellationToken);

    /// <summary>POST drivers/models/check: Check for updates on every model.</summary>
    [Function("CheckAllDriverModelUpdates")]
    public Task<HttpResponseData> CheckAllDriverModelUpdates(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/check")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "models/check", context.CancellationToken);

    /// <summary>POST drivers/models/{id}/check: Check for updates on one model.</summary>
    [Function("CheckDriverModelUpdates")]
    public Task<HttpResponseData> CheckDriverModelUpdates(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/{id}/check")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? ForwardAsync(req, HttpMethod.Post, $"models/{modelId:D}/check", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>PUT drivers/vendors/{vendor}: Sync on or off.</summary>
    [Function("SetDriverVendor")]
    public Task<HttpResponseData> SetDriverVendor(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "drivers/vendors/{vendor}")] HttpRequestData req,
        string vendor,
        FunctionContext context) =>
        IsSafeSegment(vendor)
            ? ForwardAsync(req, HttpMethod.Put, $"vendors/{vendor}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>GET drivers/sync/settings.</summary>
    [Function("GetDriverSyncSettings")]
    public Task<HttpResponseData> GetDriverSyncSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/sync/settings")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "sync/settings", context.CancellationToken);

    /// <summary>PUT drivers/sync/settings.</summary>
    [Function("SetDriverSyncSettings")]
    public Task<HttpResponseData> SetDriverSyncSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "drivers/sync/settings")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Put, "sync/settings", context.CancellationToken);

    /// <summary>GET drivers/sync/runs/{id}/items.</summary>
    [Function("GetDriverSyncRunItems")]
    public Task<HttpResponseData> GetDriverSyncRunItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/sync/runs/{id}/items")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var runId)
            ? ForwardAsync(req, HttpMethod.Get, $"sync/runs/{runId:D}/items", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>GET drivers/sync/runs/{id}/progress.</summary>
    [Function("GetDriverSyncRunProgress")]
    public Task<HttpResponseData> GetDriverSyncRunProgress(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/sync/runs/{id}/progress")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var runId)
            ? ForwardAsync(req, HttpMethod.Get, $"sync/runs/{runId:D}/progress", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>GET drivers/reports/sync?from=&amp;to=: the Driver Synchronization report.</summary>
    [Function("GetDriverSyncReport")]
    public Task<HttpResponseData> GetDriverSyncReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/reports/sync")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "reports/sync", context.CancellationToken);

    /// <summary>GET drivers/rules.</summary>
    [Function("GetDriverRules")]
    public Task<HttpResponseData> GetDriverRules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/rules")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "rules", context.CancellationToken);

    /// <summary>GET drivers/rules/suggestions.</summary>
    [Function("GetDriverRuleSuggestions")]
    public Task<HttpResponseData> GetDriverRuleSuggestions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/rules/suggestions")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "rules/suggestions", context.CancellationToken);

    /// <summary>POST drivers/rules.</summary>
    [Function("CreateDriverRule")]
    public Task<HttpResponseData> CreateDriverRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/rules")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "rules", context.CancellationToken);

    /// <summary>PUT drivers/rules/{id}.</summary>
    [Function("UpdateDriverRule")]
    public Task<HttpResponseData> UpdateDriverRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "drivers/rules/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var ruleId)
            ? ForwardAsync(req, HttpMethod.Put, $"rules/{ruleId:D}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>DELETE drivers/rules/{id}?changedBy=.</summary>
    [Function("DeleteDriverRule")]
    public Task<HttpResponseData> DeleteDriverRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "drivers/rules/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var ruleId)
            ? ForwardAsync(req, HttpMethod.Delete, $"rules/{ruleId:D}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>GET drivers/categories?vendor=.</summary>
    [Function("GetDriverCategories")]
    public Task<HttpResponseData> GetDriverCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/categories")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "categories", context.CancellationToken);

    /// <summary>PUT drivers/critical-categories/{vendor}.</summary>
    [Function("SetDriverCriticalCategories")]
    public Task<HttpResponseData> SetDriverCriticalCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "drivers/critical-categories/{vendor}")] HttpRequestData req,
        string vendor,
        FunctionContext context) =>
        IsSafeSegment(vendor)
            ? ForwardAsync(req, HttpMethod.Put, $"critical-categories/{vendor}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>PUT drivers/models/{id}/releases/{release}/overrides/{key}.</summary>
    [Function("SetDriverOverride")]
    public Task<HttpResponseData> SetDriverOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "drivers/models/{id}/releases/{release}/overrides/{key}")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context) =>
        PairPath(id, release, key) is { } path
            ? ForwardAsync(req, HttpMethod.Put, $"{path}/overrides/{key}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>DELETE drivers/models/{id}/releases/{release}/overrides/{key}?changedBy=.</summary>
    [Function("ClearDriverOverride")]
    public Task<HttpResponseData> ClearDriverOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "drivers/models/{id}/releases/{release}/overrides/{key}")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context) =>
        PairPath(id, release, key) is { } path
            ? ForwardAsync(req, HttpMethod.Delete, $"{path}/overrides/{key}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/models/{id}/releases/{release}/approvals.</summary>
    [Function("ApproveDriverComponents")]
    public Task<HttpResponseData> ApproveDriverComponents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/{id}/releases/{release}/approvals")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context) =>
        PairPath(id, release, null) is { } path
            ? ForwardAsync(req, HttpMethod.Post, $"{path}/approvals", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/models/{id}/releases/{release}/build: Build now.</summary>
    [Function("BuildDriverSetNow")]
    public Task<HttpResponseData> BuildDriverSetNow(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/{id}/releases/{release}/build")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context) =>
        PairPath(id, release, null) is { } path
            ? ForwardAsync(req, HttpMethod.Post, $"{path}/build", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/models/{id}/releases/{release}/rollback: Roll back driver set.</summary>
    [Function("RollBackDriverSet")]
    public Task<HttpResponseData> RollBackDriverSet(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/{id}/releases/{release}/rollback")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context) =>
        PairPath(id, release, null) is { } path
            ? ForwardAsync(req, HttpMethod.Post, $"{path}/rollback", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/models/{id}/releases/{release}/testing/release: Release the set in testing.</summary>
    [Function("ReleaseDriverSetInTesting")]
    public Task<HttpResponseData> ReleaseDriverSetInTesting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/models/{id}/releases/{release}/testing/release")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context) =>
        PairPath(id, release, null) is { } path
            ? ForwardAsync(req, HttpMethod.Post, $"{path}/testing/release", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>GET drivers/models/{id}/removal-impact?release=.</summary>
    [Function("GetDriverRemovalImpact")]
    public Task<HttpResponseData> GetDriverRemovalImpact(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/models/{id}/removal-impact")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? ForwardAsync(req, HttpMethod.Get, $"models/{modelId:D}/removal-impact", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>DELETE drivers/models/{id}?changedBy=: Remove model.</summary>
    [Function("RemoveDriverModel")]
    public Task<HttpResponseData> RemoveDriverModel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "drivers/models/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? ForwardAsync(req, HttpMethod.Delete, $"models/{modelId:D}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>DELETE drivers/models/{id}/releases/{release}?changedBy=.</summary>
    [Function("RemoveDriverModelRelease")]
    public Task<HttpResponseData> RemoveDriverModelRelease(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "drivers/models/{id}/releases/{release}")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context) =>
        PairPath(id, release, null) is { } path
            ? ForwardAsync(req, HttpMethod.Delete, path, context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>DELETE drivers/models/{id}/releases/{release}/components/{key}?changedBy=: remove a withdrawn component.</summary>
    [Function("RemoveWithdrawnDriverComponent")]
    public Task<HttpResponseData> RemoveWithdrawnDriverComponent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "drivers/models/{id}/releases/{release}/components/{key}")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context) =>
        PairPath(id, release, key) is { } path
            ? ForwardAsync(req, HttpMethod.Delete, $"{path}/components/{key}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>GET drivers/storage.</summary>
    [Function("GetDriverStorage")]
    public Task<HttpResponseData> GetDriverStorage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/storage")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "storage", context.CancellationToken);

    /// <summary>PUT drivers/storage/settings: previous versions to keep.</summary>
    [Function("SetDriverStorageSettings")]
    public Task<HttpResponseData> SetDriverStorageSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "drivers/storage/settings")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Put, "storage/settings", context.CancellationToken);

    /// <summary>GET drivers/storage/models/{id}.</summary>
    [Function("GetDriverStorageModel")]
    public Task<HttpResponseData> GetDriverStorageModel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/storage/models/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? ForwardAsync(req, HttpMethod.Get, $"storage/models/{modelId:D}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>GET drivers/downloads.</summary>
    [Function("GetDriverDownloads")]
    public Task<HttpResponseData> GetDriverDownloads(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/downloads")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "downloads", context.CancellationToken);

    /// <summary>POST drivers/assignment-preview: the Driver set column on the Devices page; reads only.</summary>
    [Function("PreviewDriverAssignment")]
    public Task<HttpResponseData> PreviewDriverAssignment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/assignment-preview")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "assignment-preview", context.CancellationToken);

    /// <summary>GET drivers/models/{id}/releases/{release}/components/{key}/versions[?check=true].</summary>
    [Function("GetDriverComponentVersions")]
    public Task<HttpResponseData> GetDriverComponentVersions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/models/{id}/releases/{release}/components/{key}/versions")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context) =>
        PairPath(id, release, key) is { } path
            ? ForwardAsync(req, HttpMethod.Get, $"{path}/components/{key}/versions", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>PUT drivers/models/{id}/releases/{release}/components/{key}/rollback: Roll back component.</summary>
    [Function("SetDriverComponentRollBack")]
    public Task<HttpResponseData> SetDriverComponentRollBack(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "drivers/models/{id}/releases/{release}/components/{key}/rollback")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context) =>
        PairPath(id, release, key) is { } path
            ? ForwardAsync(req, HttpMethod.Put, $"{path}/components/{key}/rollback", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>DELETE drivers/models/{id}/releases/{release}/components/{key}/rollback?changedBy=: Undo roll back.</summary>
    [Function("ClearDriverComponentRollBack")]
    public Task<HttpResponseData> ClearDriverComponentRollBack(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "drivers/models/{id}/releases/{release}/components/{key}/rollback")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context) =>
        PairPath(id, release, key) is { } path
            ? ForwardAsync(req, HttpMethod.Delete, $"{path}/components/{key}/rollback", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/custom-components/uploads/start.</summary>
    [Function("StartCustomComponentUpload")]
    public Task<HttpResponseData> StartCustomComponentUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/custom-components/uploads/start")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "custom-components/uploads/start", context.CancellationToken);

    /// <summary>POST drivers/custom-components/uploads/{id}/complete.</summary>
    [Function("CompleteCustomComponentUpload")]
    public Task<HttpResponseData> CompleteCustomComponentUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/custom-components/uploads/{id}/complete")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var uploadId)
            ? ForwardAsync(req, HttpMethod.Post, $"custom-components/uploads/{uploadId:D}/complete", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/custom-components/uploads/{id}/abandon.</summary>
    [Function("AbandonCustomComponentUpload")]
    public Task<HttpResponseData> AbandonCustomComponentUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/custom-components/uploads/{id}/abandon")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var uploadId)
            ? ForwardAsync(req, HttpMethod.Post, $"custom-components/uploads/{uploadId:D}/abandon", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/custom-components.</summary>
    [Function("CreateCustomComponent")]
    public Task<HttpResponseData> CreateCustomComponent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/custom-components")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "custom-components", context.CancellationToken);

    /// <summary>GET drivers/custom-components.</summary>
    [Function("GetCustomComponents")]
    public Task<HttpResponseData> GetCustomComponents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "drivers/custom-components")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "custom-components", context.CancellationToken);

    /// <summary>PUT drivers/custom-components/{id}.</summary>
    [Function("UpdateCustomComponent")]
    public Task<HttpResponseData> UpdateCustomComponent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "drivers/custom-components/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var customId)
            ? ForwardAsync(req, HttpMethod.Put, $"custom-components/{customId:D}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>DELETE drivers/custom-components/{id}?changedBy=.</summary>
    [Function("DeleteCustomComponent")]
    public Task<HttpResponseData> DeleteCustomComponent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "drivers/custom-components/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var customId)
            ? ForwardAsync(req, HttpMethod.Delete, $"custom-components/{customId:D}", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>POST drivers/custom-components/{id}/notices/{modelId}/dismiss.</summary>
    [Function("DismissCustomComponentNotice")]
    public Task<HttpResponseData> DismissCustomComponentNotice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "drivers/custom-components/{id}/notices/{modelId}/dismiss")] HttpRequestData req,
        string id,
        string modelId,
        FunctionContext context) =>
        Guid.TryParse(id, out var customId) && Guid.TryParse(modelId, out var model)
            ? ForwardAsync(req, HttpMethod.Post, $"custom-components/{customId:D}/notices/{model:D}/dismiss", context.CancellationToken)
            : Task.FromResult(BadSegment(req));

    /// <summary>GET media-builder/driver-sets?architecture=: sets Media Builder can preload; MediaBuilderAccess only.</summary>
    [Function("GetDriverSetsForMediaBuilder")]
    public Task<HttpResponseData> GetDriverSetsForMediaBuilder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "media-builder/driver-sets")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Get, "media-builder/sets", context.CancellationToken);

    /// <summary>POST media-builder/driver-sets/links: read links for chosen sets; MediaBuilderAccess only.</summary>
    [Function("GetDriverSetLinksForMediaBuilder")]
    public Task<HttpResponseData> GetDriverSetLinksForMediaBuilder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "media-builder/driver-sets/links")] HttpRequestData req,
        FunctionContext context) =>
        ForwardAsync(req, HttpMethod.Post, "media-builder/links", context.CancellationToken);

    /// <summary><c>models/{id}/releases/{release}</c> when the id, release and optional lineage key are valid segments; otherwise null.</summary>
    internal static string? PairPath(string id, string release, string? lineageKey) =>
        Guid.TryParse(id, out var modelId) && IsSafeSegment(release) && (lineageKey is null || IsSafeSegment(lineageKey))
            ? $"models/{modelId:D}/releases/{release}"
            : null;

    /// <summary>Forwards a request and relays Core's answer.</summary>
    internal async Task<HttpResponseData> ForwardAsync(HttpRequestData req, HttpMethod method, string path, CancellationToken ct)
    {
        string? body = null;
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            using var reader = new StreamReader(req.Body);
            body = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
            {
                body = null;
            }
        }

        var query = string.IsNullOrEmpty(req.Url.Query) ? null : req.Url.Query;
        return await ProxyResponseAsync(req, await _coreClient.ForwardDriversAsync(method, path, query, body, ct), ct);
    }

    /// <summary>400 without an upstream call, for a segment that failed validation.</summary>
    internal static HttpResponseData BadSegment(HttpRequestData req) => req.CreateResponse(HttpStatusCode.BadRequest);

    private static async Task<HttpResponseData> ProxyResponseAsync(HttpRequestData req, HttpResponseMessage coreResponse, CancellationToken ct)
    {
        using (coreResponse)
        {
            var response = req.CreateResponse((HttpStatusCode)(int)coreResponse.StatusCode);
            if (coreResponse.Headers.RetryAfter?.Delta is { } retryAfter)
            {
                response.Headers.Add("Retry-After", ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var content = await coreResponse.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(content))
            {
                var mediaType = coreResponse.Content.Headers.ContentType?.MediaType;
                response.Headers.Add("Content-Type", mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) ? mediaType : "text/plain");
                await response.WriteStringAsync(content, ct);
            }

            return response;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.-]{1,80}$")]
    private static partial Regex SafeSegment();
}
