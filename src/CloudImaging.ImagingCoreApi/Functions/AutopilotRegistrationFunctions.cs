using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal Autopilot registration endpoints. Device-facing calls arrive from the Device Gateway,
/// portal-facing calls from the Operator API; both over Private Link.
///
/// GET  /api/internal/autopilot/availability                      device: is the feature on
/// POST /api/internal/autopilot/registrations                     device: submit a hardware hash
/// POST /api/internal/autopilot/registrations/{id}/device-status  device: poll with its status token
/// GET  /api/internal/autopilot/registrations                     portal: the open approval queue
/// GET  /api/internal/autopilot/registrations?view=history&amp;from=&amp;to= portal: handled requests closed in the range
/// GET  /api/internal/autopilot/registrations/{id}                portal: detail with group tag options
/// POST /api/internal/autopilot/registrations/{id}/approve        portal
/// POST /api/internal/autopilot/registrations/{id}/reject         portal
/// POST /api/internal/autopilot/registrations/{id}/retry          portal
/// </summary>
public sealed class AutopilotRegistrationFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AutopilotRegistrationService _service;

    public AutopilotRegistrationFunctions(AutopilotRegistrationService service) => _service = service;

    [Function("GetAutopilotAvailability")]
    public async Task<HttpResponseData> GetAvailability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/autopilot/availability")] HttpRequestData req,
        FunctionContext context)
    {
        var enabled = await _service.IsEnabledAsync(context.CancellationToken);
        return await JsonAsync(req, HttpStatusCode.OK, new AutopilotAvailability { Enabled = enabled }, context.CancellationToken);
    }

    [Function("SubmitAutopilotRegistration")]
    public async Task<HttpResponseData> Submit(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/autopilot/registrations")] HttpRequestData req,
        FunctionContext context)
    {
        var submission = await ReadAsync<AutopilotHashSubmission>(req, context.CancellationToken);
        if (submission is null)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid submission payload.", context.CancellationToken);
        }

        var result = await _service.SubmitAsync(submission, context.CancellationToken);
        return result.Outcome == AutopilotOutcome.Ok
            ? await JsonAsync(req, HttpStatusCode.Created, result.Value!, context.CancellationToken)
            : await ProblemAsync(req, StatusFor(result.Outcome), result.Error!, context.CancellationToken);
    }

    [Function("GetAutopilotDeviceStatus")]
    public async Task<HttpResponseData> GetDeviceStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/autopilot/registrations/{id}/device-status")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        var body = await ReadAsync<DeviceStatusQuery>(req, context.CancellationToken);
        if (!Guid.TryParse(id, out var requestId) || string.IsNullOrWhiteSpace(body?.StatusToken))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "A request id and status token are required.", context.CancellationToken);
        }

        // A wrong token and an unknown id both return 404 so the endpoint cannot be used to probe ids.
        var status = await _service.GetDeviceStatusAsync(requestId, body.StatusToken, context.CancellationToken);
        return status is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, "Registration request not found.", context.CancellationToken)
            : await JsonAsync(req, HttpStatusCode.OK, status, context.CancellationToken);
    }

    [Function("ListAutopilotRegistrations")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/autopilot/registrations")] HttpRequestData req,
        FunctionContext context)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        if (!string.Equals(query.Get("view"), "history", StringComparison.OrdinalIgnoreCase))
        {
            return await JsonAsync(req, HttpStatusCode.OK, await _service.ListOpenAsync(context.CancellationToken), context.CancellationToken);
        }

        if (!TryParseBound(query.Get("from"), DateTimeOffset.MinValue, out var from) || !TryParseBound(query.Get("to"), DateTimeOffset.MaxValue, out var to) || from > to)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "from and to must be ISO 8601 timestamps, with from no later than to.", context.CancellationToken);
        }

        var requests = await _service.ListHandledAsync(from, to, context.CancellationToken);
        return await JsonAsync(req, HttpStatusCode.OK, requests, context.CancellationToken);
    }

    private static bool TryParseBound(string? value, DateTimeOffset fallback, out DateTimeOffset result)
    {
        if (string.IsNullOrEmpty(value))
        {
            result = fallback;
            return true;
        }

        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out result);
    }

    [Function("GetAutopilotRegistration")]
    public async Task<HttpResponseData> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/autopilot/registrations/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var requestId))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid request id.", context.CancellationToken);
        }

        var detail = await _service.GetDetailAsync(requestId, context.CancellationToken);
        return detail is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, "Registration request not found.", context.CancellationToken)
            : await JsonAsync(req, HttpStatusCode.OK, detail, context.CancellationToken);
    }

    [Function("ApproveAutopilotRegistration")]
    public Task<HttpResponseData> Approve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/autopilot/registrations/{id}/approve")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        DecideAsync(req, id, _service.ApproveAsync, context.CancellationToken);

    [Function("RejectAutopilotRegistration")]
    public Task<HttpResponseData> Reject(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/autopilot/registrations/{id}/reject")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        DecideAsync(req, id, _service.RejectAsync, context.CancellationToken);

    [Function("RetryAutopilotRegistration")]
    public Task<HttpResponseData> Retry(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/autopilot/registrations/{id}/retry")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        DecideAsync(req, id, _service.RetryAsync, context.CancellationToken);

    private static async Task<HttpResponseData> DecideAsync(
        HttpRequestData req,
        string id,
        Func<Guid, AutopilotDecision, CancellationToken, Task<AutopilotResult<AutopilotRegistrationRequest>>> action,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var requestId))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid request id.", ct);
        }

        var decision = await ReadAsync<AutopilotDecision>(req, ct);
        if (decision is null || string.IsNullOrWhiteSpace(decision.DecidedByUpn))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, "The deciding user is required.", ct);
        }

        var result = await action(requestId, decision, ct);

        // A failed Graph import still records the decision; the caller reads ImportFailed and its error from the body.
        return result.Outcome == AutopilotOutcome.Ok || (result.Outcome == AutopilotOutcome.GraphFailed && result.Value is not null)
            ? await JsonAsync(req, HttpStatusCode.OK, result.Value!, ct)
            : await ProblemAsync(req, StatusFor(result.Outcome), result.Error!, ct);
    }

    internal static HttpStatusCode StatusFor(AutopilotOutcome outcome) => outcome switch
    {
        AutopilotOutcome.NotFound => HttpStatusCode.NotFound,
        AutopilotOutcome.Conflict => HttpStatusCode.Conflict,
        AutopilotOutcome.Invalid => HttpStatusCode.BadRequest,
        AutopilotOutcome.Disabled => HttpStatusCode.Forbidden,
        AutopilotOutcome.GraphFailed => HttpStatusCode.BadGateway,
        _ => HttpStatusCode.InternalServerError,
    };

    private static async Task<T?> ReadAsync<T>(HttpRequestData req, CancellationToken ct)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task<HttpResponseData> JsonAsync<T>(HttpRequestData req, HttpStatusCode status, T body, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions), ct);
        return response;
    }

    internal static async Task<HttpResponseData> ProblemAsync(HttpRequestData req, HttpStatusCode status, string detail, CancellationToken ct)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/problem+json");
        var problem = new { title = status.ToString(), status = (int)status, detail };
        await response.WriteStringAsync(JsonSerializer.Serialize(problem, JsonOptions), ct);
        return response;
    }

    private sealed class DeviceStatusQuery
    {
        public string? StatusToken { get; init; }
    }
}
