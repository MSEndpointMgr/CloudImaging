using System.Net;
using System.Web;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Storage, downloads, roll back and removal endpoints, called by the Operator API on behalf of
/// the portal.
///
/// GET    internal/drivers/storage                                       Storage tab
/// PUT    internal/drivers/storage/settings                              previous versions to keep
/// GET    internal/drivers/storage/models/{id}                           one model on the Storage tab
/// GET    internal/drivers/downloads                                     Downloads tab
/// POST   internal/drivers/models/{id}/releases/{release}/rollback       Roll back driver set
/// POST   internal/drivers/models/{id}/releases/{release}/testing/release   Release the set in testing
/// GET    internal/drivers/models/{id}/removal-impact?release=           what a removal deletes and frees
/// DELETE internal/drivers/models/{id}?changedBy=                        Remove model
/// DELETE internal/drivers/models/{id}/releases/{release}?changedBy=     remove a release
/// DELETE internal/drivers/models/{id}/releases/{release}/components/{key}?changedBy=   remove a withdrawn component
/// </summary>
public sealed class DriverLifecycleFunctions
{
    private readonly DriverLifecycleService _lifecycle;

    /// <param name="lifecycle">Storage, downloads, roll back and removal.</param>
    public DriverLifecycleFunctions(DriverLifecycleService lifecycle) => _lifecycle = lifecycle;

    /// <summary>GET internal/drivers/storage.</summary>
    [Function("GetDriverStorage")]
    public async Task<HttpResponseData> GetDriverStorage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/storage")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _lifecycle.GetStorageAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>PUT internal/drivers/storage/settings.</summary>
    [Function("SetDriverStorageSettings")]
    public async Task<HttpResponseData> SetDriverStorageSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/drivers/storage/settings")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.ResultAsync(req, await _lifecycle.SetStorageSettingsAsync(await DriverHttp.ReadAsync<SetDriverStorageSettingsRequest>(req, context.CancellationToken), context.CancellationToken), context.CancellationToken);

    /// <summary>GET internal/drivers/storage/models/{id}.</summary>
    [Function("GetDriverStorageModel")]
    public async Task<HttpResponseData> GetDriverStorageModel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/storage/models/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? await DriverHttp.ResultAsync(req, await _lifecycle.GetStorageModelAsync(modelId, context.CancellationToken), context.CancellationToken)
            : await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", context.CancellationToken);

    /// <summary>GET internal/drivers/downloads.</summary>
    [Function("GetDriverDownloads")]
    public async Task<HttpResponseData> GetDriverDownloads(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/downloads")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _lifecycle.GetDownloadsAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>POST internal/drivers/models/{id}/releases/{release}/rollback.</summary>
    [Function("RollBackDriverSet")]
    public async Task<HttpResponseData> RollBackDriverSet(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/{id}/releases/{release}/rollback")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, null, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _lifecycle.RollBackSetAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, request?.Actor, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models/{id}/releases/{release}/testing/release.</summary>
    [Function("ReleaseDriverSetInTesting")]
    public async Task<HttpResponseData> ReleaseDriverSetInTesting(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/{id}/releases/{release}/testing/release")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, null, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _lifecycle.ReleaseTestingAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, request?.Actor, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET internal/drivers/models/{id}/removal-impact?release=.</summary>
    [Function("GetDriverRemovalImpact")]
    public async Task<HttpResponseData> GetDriverRemovalImpact(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/models/{id}/removal-impact")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var modelId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", context.CancellationToken);
        }

        var releaseText = HttpUtility.ParseQueryString(req.Url.Query)["release"];
        var release = DriverModelService.ParseRelease(releaseText);
        if (releaseText is { Length: > 0 } && release is null)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Unknown Windows release.", context.CancellationToken);
        }

        return await DriverHttp.ResultAsync(req, await _lifecycle.RemovalImpactAsync(modelId, release, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>DELETE internal/drivers/models/{id}?changedBy=.</summary>
    [Function("RemoveDriverModel")]
    public async Task<HttpResponseData> RemoveDriverModel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/drivers/models/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var modelId)
            ? await DriverHttp.ResultAsync(req, await _lifecycle.RemoveModelAsync(modelId, DriverHttp.ChangedBy(req), context.CancellationToken), context.CancellationToken)
            : await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", context.CancellationToken);

    /// <summary>DELETE internal/drivers/models/{id}/releases/{release}?changedBy=.</summary>
    [Function("RemoveDriverModelRelease")]
    public async Task<HttpResponseData> RemoveDriverModelRelease(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/drivers/models/{id}/releases/{release}")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, null, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        return await DriverHttp.ResultAsync(req, await _lifecycle.RemoveReleaseAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, DriverHttp.ChangedBy(req), context.CancellationToken), context.CancellationToken);
    }

    /// <summary>DELETE internal/drivers/models/{id}/releases/{release}/components/{key}?changedBy=.</summary>
    [Function("RemoveWithdrawnDriverComponent")]
    public async Task<HttpResponseData> RemoveWithdrawnDriverComponent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/drivers/models/{id}/releases/{release}/components/{key}")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, key, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        return await DriverHttp.ResultAsync(req, await _lifecycle.RemoveWithdrawnComponentAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, key, DriverHttp.ChangedBy(req), context.CancellationToken), context.CancellationToken);
    }
}
