using System.Net;
using System.Web;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Driver sets Media Builder can preload on a USB drive (contracts/media-builder-driver-sets.md).
///
/// GET  internal/drivers/media-builder/sets?architecture=   the current set of every active model and release
/// POST internal/drivers/media-builder/links                 read links for chosen sets, one per distinct file
///
/// Both are reads: nothing is pinned or written.
/// </summary>
public sealed class MediaBuilderDriverSetFunctions
{
    /// <summary>Most sets per links request.</summary>
    public const int MaxSets = 20;

    private readonly DriverModelRepository _models;
    private readonly DriverSetPointerRepository _pointers;
    private readonly DriverSetDocumentRepository _sets;
    private readonly DriverStorageService _storage;

    /// <param name="models">Models.</param>
    /// <param name="pointers">Current set per model and release.</param>
    /// <param name="sets">Set documents.</param>
    /// <param name="storage">Read links.</param>
    public MediaBuilderDriverSetFunctions(DriverModelRepository models, DriverSetPointerRepository pointers, DriverSetDocumentRepository sets, DriverStorageService storage)
    {
        _models = models;
        _pointers = pointers;
        _sets = sets;
        _storage = storage;
    }

    /// <summary>GET internal/drivers/media-builder/sets.</summary>
    [Function("GetMediaBuilderDriverSets")]
    public async Task<HttpResponseData> GetSets(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/media-builder/sets")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var text = HttpUtility.ParseQueryString(req.Url.Query)["architecture"];
        if (!Enum.TryParse<MachineArchitecture>(text, ignoreCase: true, out var architecture) || !Enum.IsDefined(architecture) || int.TryParse(text, out _))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Use architecture X64 or Arm64.", ct);
        }

        var result = new List<MediaBuilderDriverSet>();
        foreach (var model in (await _models.ListAsync(ct)).Where(m => m.State == DriverModelState.Active && m.Architecture == architecture))
        {
            foreach (var release in model.Releases)
            {
                if ((await _pointers.GetAsync(model.ModelId, release, ct))?.Value.CurrentVersion is not { } version
                    || await _sets.GetAsync(model.ModelId, release, version, ct) is not { } document)
                {
                    continue;
                }

                var files = document.Items.DistinctBy(i => i.Sha256, StringComparer.OrdinalIgnoreCase).ToList();
                result.Add(new MediaBuilderDriverSet
                {
                    ModelId = model.ModelId,
                    Vendor = model.Vendor,
                    VendorName = DriverSyncService.VendorName(model.Vendor),
                    ModelName = model.DisplayName,
                    Release = release,
                    ReleaseName = DriverModelService.ReleaseLabel(release),
                    SetVersion = version,
                    Components = document.Items.Count,
                    Bytes = files.Sum(i => i.Size),
                    Files = files.Select(i => new MediaBuilderDriverSetFile(i.Sha256.ToLowerInvariant(), i.Size)).ToList(),
                });
            }
        }

        var ordered = result.OrderBy(s => s.VendorName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.ModelName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Release).ToList();
        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, ordered, ct);
    }

    /// <summary>POST internal/drivers/media-builder/links.</summary>
    [Function("GetMediaBuilderDriverSetLinks")]
    public async Task<HttpResponseData> GetLinks(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/media-builder/links")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var request = await DriverHttp.ReadAsync<MediaBuilderDriverSetLinksRequest>(req, ct);
        if (request is null || request.Sets.Count == 0)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Choose at least one driver set.", ct);
        }

        if (request.Sets.Count > MaxSets)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, $"Choose at most {MaxSets} driver sets.", ct);
        }

        var models = (await _models.ListAsync(ct)).ToDictionary(m => m.ModelId);
        var sets = new List<MediaBuilderDriverSetVersion>();
        var items = new Dictionary<string, DriverSetItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var chosen in request.Sets.Distinct())
        {
            if (!models.TryGetValue(chosen.ModelId, out var model) || model.State != DriverModelState.Active || !model.Releases.Contains(chosen.Release)
                || (await _pointers.GetAsync(chosen.ModelId, chosen.Release, ct))?.Value.CurrentVersion is not { } version
                || await _sets.GetAsync(chosen.ModelId, chosen.Release, version, ct) is not { } document)
            {
                return await DriverHttp.ProblemAsync(req, HttpStatusCode.NotFound, "A chosen driver set is no longer available. Reload the list.", ct);
            }

            sets.Add(new MediaBuilderDriverSetVersion { ModelId = chosen.ModelId, Release = chosen.Release, SetVersion = version });
            foreach (var item in document.Items)
            {
                items.TryAdd(item.Sha256, item);
            }
        }

        var files = new List<MediaBuilderDriverFile>();
        foreach (var item in items.Values)
        {
            var (url, expiresAt) = await _storage.GetReadLinkAsync(item.BlobName, ct);
            files.Add(new MediaBuilderDriverFile { Sha256 = item.Sha256.ToLowerInvariant(), FileName = item.FileName, SizeBytes = item.Size, Url = url, ExpiresAt = expiresAt });
        }

        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, new MediaBuilderDriverSetLinks { Sets = sets, Files = files }, ct);
    }
}
