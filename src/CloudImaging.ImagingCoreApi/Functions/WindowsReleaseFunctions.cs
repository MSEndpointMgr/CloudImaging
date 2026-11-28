using System.Net;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// GET internal/drivers/windows-releases: the Windows releases the portal offers for OS images and
/// models, called by the Operator API on behalf of the portal.
/// </summary>
public sealed class WindowsReleaseFunctions
{
    private readonly WindowsReleaseCatalogService _releases;

    /// <param name="releases">The release catalog.</param>
    public WindowsReleaseFunctions(WindowsReleaseCatalogService releases)
    {
        _releases = releases;
    }

    /// <summary>GET internal/drivers/windows-releases.</summary>
    [Function("GetWindowsReleases")]
    public async Task<HttpResponseData> GetWindowsReleases(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/windows-releases")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _releases.GetAsync(context.CancellationToken), context.CancellationToken);
}
