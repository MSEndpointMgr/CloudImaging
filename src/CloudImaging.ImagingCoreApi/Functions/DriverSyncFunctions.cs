using System.Globalization;
using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Driver sync: the <c>driver-work</c> queue triggers (there is no polling timer) and the run
/// endpoints, called by the Operator API on behalf of the portal.
///
/// POST internal/drivers/sync/runs    Sync now
/// GET  internal/drivers/sync/runs    runs of the last 90 days, newest first
/// GET  internal/drivers/sync/runs/{id}/items   one line per change of a run
/// GET  internal/drivers/sync/runs/{id}/progress   stages, found so far and latest items of a run
/// GET  internal/drivers/reports/sync?from=&amp;to=   runs and their items, for the report
/// GET  internal/drivers/sync/settings          schedule, next slot, time zones
/// PUT  internal/drivers/sync/settings          save the schedule
/// POST internal/drivers/models/check        Check for updates, every model
/// POST internal/drivers/models/{id}/check   Check for updates, one model
/// </summary>
public sealed partial class DriverSyncFunctions
{
    private readonly DriverSyncService _sync;
    private readonly DriverSyncRunRepository _runs;
    private readonly DriverSyncScheduler _scheduler;
    private readonly ILogger<DriverSyncFunctions> _logger;

    /// <param name="sync">Sync engine.</param>
    /// <param name="runs">Runs.</param>
    /// <param name="scheduler">Schedule and settings.</param>
    /// <param name="logger">Logger.</param>
    public DriverSyncFunctions(DriverSyncService sync, DriverSyncRunRepository runs, DriverSyncScheduler scheduler, ILogger<DriverSyncFunctions> logger)
    {
        _sync = sync;
        _runs = runs;
        _scheduler = scheduler;
        _logger = logger;
    }

    /// <summary>GET internal/drivers/sync/settings.</summary>
    [Function("GetDriverSyncSettings")]
    public async Task<HttpResponseData> GetDriverSyncSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/sync/settings")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _scheduler.GetSettingsAsync(true, context.CancellationToken), context.CancellationToken);

    /// <summary>PUT internal/drivers/sync/settings.</summary>
    [Function("SetDriverSyncSettings")]
    public async Task<HttpResponseData> SetDriverSyncSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/drivers/sync/settings")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.ResultAsync(req, await _scheduler.SetSettingsAsync(await DriverHttp.ReadAsync<SetDriverSyncSettingsRequest>(req, context.CancellationToken), context.CancellationToken), context.CancellationToken);

    /// <summary>GET internal/drivers/sync/runs/{id}/items.</summary>
    [Function("GetDriverSyncRunItems")]
    public async Task<HttpResponseData> GetDriverSyncRunItems(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/sync/runs/{id}/items")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var runId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid run id.", context.CancellationToken);
        }

        if (await _runs.GetAsync(runId, context.CancellationToken) is null)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.NotFound, "Run not found.", context.CancellationToken);
        }

        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _runs.ListItemsAsync(runId, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET internal/drivers/sync/runs.</summary>
    [Function("GetDriverSyncRuns")]
    public async Task<HttpResponseData> GetDriverSyncRuns(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/sync/runs")] HttpRequestData req,
        FunctionContext context)
    {
        var runs = (await _runs.ListAsync(context.CancellationToken)).Select(DriverSyncService.ToContract).ToList();
        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, runs, context.CancellationToken);
    }

    /// <summary>GET internal/drivers/sync/runs/{id}/progress.</summary>
    [Function("GetDriverSyncRunProgress")]
    public async Task<HttpResponseData> GetDriverSyncRunProgress(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/sync/runs/{id}/progress")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var runId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid run id.", context.CancellationToken);
        }

        return await _sync.GetProgressAsync(runId, context.CancellationToken) is { } progress
            ? await DriverHttp.JsonAsync(req, HttpStatusCode.OK, progress, context.CancellationToken)
            : await DriverHttp.ProblemAsync(req, HttpStatusCode.NotFound, "Run not found.", context.CancellationToken);
    }

    /// <summary>GET internal/drivers/reports/sync?from=&amp;to=: runs that started in the range, with their items.</summary>
    [Function("GetDriverSyncReport")]
    public async Task<HttpResponseData> GetDriverSyncReport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/reports/sync")] HttpRequestData req,
        FunctionContext context)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (!DateTimeOffset.TryParse(query["from"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var from)
            || !DateTimeOffset.TryParse(query["to"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var to)
            || to < from)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Enter a valid date range.", context.CancellationToken);
        }

        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _sync.GetReportAsync(from, to, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>Runs one driver work message. A throw returns it to the queue; after 3 attempts it moves to the poison queue.</summary>
    [Function("DriverWorkProcessor")]
    public async Task DriverWorkProcessor(
        [QueueTrigger(DriverWorkSender.QueueName, Connection = "AzureWebJobsStorage")] string body,
        FunctionContext context)
    {
        if (TryParse(body) is { } message)
        {
            if (message.Kind == DriverWorkKind.ScheduledSync)
            {
                await _scheduler.HandleAsync(message, context.CancellationToken);
                return;
            }

            await _sync.HandleAsync(message, context.CancellationToken);
        }
    }

    /// <summary>Records a message the host gave up on, so its run still finishes.</summary>
    [Function("DriverWorkPoison")]
    public async Task DriverWorkPoison(
        [QueueTrigger(DriverWorkSender.PoisonQueueName, Connection = "AzureWebJobsStorage")] string body,
        FunctionContext context)
    {
        if (TryParse(body) is { } message)
        {
            await _sync.HandlePoisonAsync(message, context.CancellationToken);
        }
    }

    /// <summary>POST internal/drivers/sync/runs: Sync now.</summary>
    [Function("StartDriverSyncRun")]
    public async Task<HttpResponseData> StartDriverSyncRun(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/sync/runs")] HttpRequestData req,
        FunctionContext context)
    {
        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken);
        if (request?.Actor?.Upn is not { Length: > 0 } upn)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, DriverHttp.MissingActor, context.CancellationToken);
        }

        return await DriverHttp.RunStartedAsync(req, await _sync.StartRunAsync(DriverSyncTrigger.Manual, upn, null, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models/check: Check for updates on the Models tab (components only, every onboarded model).</summary>
    [Function("CheckAllDriverModelUpdates")]
    public async Task<HttpResponseData> CheckAllDriverModelUpdates(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/check")] HttpRequestData req,
        FunctionContext context)
    {
        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken);
        if (request?.Actor?.Upn is not { Length: > 0 } upn)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, DriverHttp.MissingActor, context.CancellationToken);
        }

        return await DriverHttp.RunStartedAsync(req, await _sync.StartRunAsync(DriverSyncTrigger.Check, upn, null, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models/{id}/check: Check for updates on one model.</summary>
    [Function("CheckDriverModelUpdates")]
    public async Task<HttpResponseData> CheckDriverModelUpdates(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/{id}/check")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var modelId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", context.CancellationToken);
        }

        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken);
        if (request?.Actor?.Upn is not { Length: > 0 } upn)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, DriverHttp.MissingActor, context.CancellationToken);
        }

        return await DriverHttp.RunStartedAsync(req, await _sync.StartRunAsync(DriverSyncTrigger.ModelCheck, upn, modelId, context.CancellationToken), context.CancellationToken);
    }

    // A message that is not valid JSON can never succeed, so it is dropped rather than retried.
    private DriverWorkMessage? TryParse(string body)
    {
        try
        {
            return DriverWorkSender.Parse(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            LogUnreadable(_logger, ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Dropped a driver work message that could not be read.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);
}

/// <summary>JSON and problem responses shared by the driver functions.</summary>
internal static partial class DriverHttp
{
    public const string MissingActor = DriverHttpText.MissingActor;

    public const string InvalidBody = "The request body is not valid.";

    /// <summary>The acting user of a DELETE, from <c>?changedBy=</c>.</summary>
    public static string? ChangedBy(HttpRequestData req) =>
        System.Web.HttpUtility.ParseQueryString(req.Url.Query)["changedBy"] is { Length: > 0 and <= 256 } upn ? upn : null;

    /// <summary>A 400 problem when the model id, release or lineage key in a path is not valid; otherwise none.</summary>
    public static async Task<(HttpResponseData? Problem, bool Valid)> PairAsync(HttpRequestData req, string id, string release, string? lineageKey, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _))
        {
            return (await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", ct), false);
        }

        if (Services.Drivers.DriverModelService.ParseRelease(release) is null)
        {
            return (await ProblemAsync(req, HttpStatusCode.BadRequest, "Unknown Windows release.", ct), false);
        }

        if (lineageKey is not null && !LineageKey().IsMatch(lineageKey))
        {
            return (await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid component.", ct), false);
        }

        return (null, true);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_.-]{1,80}$")]
    private static partial System.Text.RegularExpressions.Regex LineageKey();

    public static async Task<HttpResponseData> ResultAsync(HttpRequestData req, DriverResult result, CancellationToken ct)
    {
        if (result.Body is { } body)
        {
            return await JsonAsync(req, result.Status, body, ct);
        }

        return result.Status is HttpStatusCode.NoContent or HttpStatusCode.Accepted
            ? req.CreateResponse(result.Status)
            : await ProblemAsync(req, result.Status, result.Detail ?? string.Empty, ct, result.Extra?.ToDictionary());
    }

    public static async Task<T?> ReadAsync<T>(HttpRequestData req, CancellationToken ct) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(req.Body, DriverJson.Options, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static async Task<HttpResponseData> JsonAsync(HttpRequestData req, HttpStatusCode status, object body, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, body.GetType(), DriverJson.Options), ct);
        return response;
    }

    public static async Task<HttpResponseData> ProblemAsync(HttpRequestData req, HttpStatusCode status, string detail, CancellationToken ct, IDictionary<string, object?>? extra = null)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        var body = new Dictionary<string, object?>
        {
            ["type"] = "https://cloudimaging.io/errors/drivers",
            ["title"] = status.ToString(),
            ["status"] = (int)status,
            ["detail"] = detail,
        };
        foreach (var (key, value) in extra ?? new Dictionary<string, object?>())
        {
            body[key] = value;
        }

        await response.WriteStringAsync(JsonSerializer.Serialize(body, DriverJson.Options), ct);
        return response;
    }

    // 202 with the run id, or the refusal with availableAt and a Retry-After header.
    public static async Task<HttpResponseData> RunStartedAsync(HttpRequestData req, DriverRunStart start, CancellationToken ct)
    {
        var status = start.Status switch
        {
            DriverRunStartStatus.Started => HttpStatusCode.Accepted,
            DriverRunStartStatus.TooSoon => HttpStatusCode.TooManyRequests,
            DriverRunStartStatus.NotFound => HttpStatusCode.NotFound,
            _ => HttpStatusCode.Conflict,
        };
        if (status == HttpStatusCode.Accepted)
        {
            return await JsonAsync(req, status, new DriverRunStarted { RunId = start.RunId!.Value }, ct);
        }

        var response = await ProblemAsync(req, status, start.Detail ?? string.Empty, ct, new Dictionary<string, object?> { ["availableAt"] = start.AvailableAt });
        if (start.AvailableAt is { } availableAt)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((availableAt - DateTimeOffset.UtcNow).TotalSeconds));
            response.Headers.Add("Retry-After", seconds.ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }
}
