using System.Net;
using System.Web;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// A component's version history and its roll back, called by the Operator API on behalf of the
/// portal.
///
/// GET    internal/drivers/models/{id}/releases/{release}/components/{key}/versions[?check=true]
/// PUT    internal/drivers/models/{id}/releases/{release}/components/{key}/rollback   { version, until, actor }
/// DELETE internal/drivers/models/{id}/releases/{release}/components/{key}/rollback?changedBy=
/// </summary>
public sealed class DriverComponentHistoryFunctions
{
    private readonly DriverComponentHistoryService _history;

    /// <param name="history">Version history and roll back.</param>
    public DriverComponentHistoryFunctions(DriverComponentHistoryService history) => _history = history;

    /// <summary>GET .../components/{key}/versions[?check=true].</summary>
    [Function("GetDriverComponentVersions")]
    public async Task<HttpResponseData> GetDriverComponentVersions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/models/{id}/releases/{release}/components/{key}/versions")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, key, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        var check = string.Equals(HttpUtility.ParseQueryString(req.Url.Query)["check"], "true", StringComparison.OrdinalIgnoreCase);
        return await DriverHttp.ResultAsync(req, await _history.VersionsAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, key, check, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>PUT .../components/{key}/rollback.</summary>
    [Function("SetDriverComponentRollBack")]
    public async Task<HttpResponseData> SetDriverComponentRollBack(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/drivers/models/{id}/releases/{release}/components/{key}/rollback")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, key, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverComponentRollBackRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _history.SetRollBackAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, key, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>DELETE .../components/{key}/rollback?changedBy=.</summary>
    [Function("ClearDriverComponentRollBack")]
    public async Task<HttpResponseData> ClearDriverComponentRollBack(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/drivers/models/{id}/releases/{release}/components/{key}/rollback")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, key, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        return await DriverHttp.ResultAsync(req, await _history.ClearRollBackAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, key, DriverHttp.ChangedBy(req), context.CancellationToken), context.CancellationToken);
    }
}
