using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// GET internal/sessions/{id}/steps/{stepName}/items. Per-item outcomes of a step, failed items
/// first, then items that did not run, then the reported order.
/// </summary>
public sealed class SessionStepItemsFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DeviceSessionRepository _sessions;
    private readonly ImagingStepItemRepository _items;

    /// <param name="sessions">Device sessions.</param>
    /// <param name="items">Step items.</param>
    public SessionStepItemsFunction(DeviceSessionRepository sessions, ImagingStepItemRepository items)
    {
        _sessions = sessions;
        _items = items;
    }

    /// <summary>GET internal/sessions/{id}/steps/{stepName}/items.</summary>
    [Function("GetSessionStepItems")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/sessions/{id}/steps/{stepName}/items")] HttpRequestData req,
        string id,
        string stepName,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(id, out var sessionId)
            || !Enum.TryParse<ImagingStepName>(stepName, ignoreCase: false, out var step)
            || !Enum.IsDefined(step)
            || int.TryParse(stepName, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }
        if (await _sessions.GetByIdAsync(sessionId, ct) is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var items = await _items.ListAsync(sessionId, step, ct);
        var sorted = items
            .OrderBy(item => item.Outcome switch { StepItemOutcome.Failed => 0, StepItemOutcome.NotRun => 1, _ => 2 })
            .ThenBy(item => item.Index)
            .ToList();

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(sorted, JsonOptions), ct);
        return response;
    }
}
