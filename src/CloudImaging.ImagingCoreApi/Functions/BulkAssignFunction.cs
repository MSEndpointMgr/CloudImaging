using System.Net;
using System.Text.Json;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST /api/internal/sessions/bulk-assign — Assigns an OS image to multiple sessions (T075, FR-035).
/// Request: { "sessionIds": ["..."], "osImageId": "..." }
/// Response: { "assigned": N, "skipped": M, "assignedIds": [...], "skippedIds": [...] }
/// </summary>
public sealed partial class BulkAssignFunction
{
    private readonly BulkAssignmentService _bulkService;
    private readonly ILogger<BulkAssignFunction> _logger;

    public BulkAssignFunction(
        BulkAssignmentService bulkService,
        ILogger<BulkAssignFunction> logger)
    {
        _bulkService = bulkService;
        _logger      = logger;
    }

    [Function(nameof(BulkAssignFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/sessions/bulk-assign")] HttpRequestData req,
        FunctionContext context)
    {
        using var body = await JsonDocument.ParseAsync(req.Body, cancellationToken: context.CancellationToken);

        if (!body.RootElement.TryGetProperty("sessionIds", out var sessionIdsProp)
            || !body.RootElement.TryGetProperty("osImageId", out var imageIdProp)
            || !Guid.TryParse(imageIdProp.GetString(), out var osImageId))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("sessionIds and osImageId (GUID) are required.", context.CancellationToken);
            return bad;
        }

        var sessionIds = sessionIdsProp.EnumerateArray()
            .Select(el => el.GetString())
            .Where(s => s is not null && Guid.TryParse(s, out _))
            .Select(s => Guid.Parse(s!))
            .ToList();

        BulkAssignmentService.BulkAssignResult result;
        try
        {
            result = await _bulkService.AssignAsync(sessionIds, osImageId, context.CancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync(ex.Message, context.CancellationToken);
            return bad;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            assigned    = result.Assigned,
            skipped     = result.Skipped,
            assignedIds = result.AssignedIds,
            skippedIds  = result.SkippedIds,
        }), context.CancellationToken);
        return response;
    }
}
