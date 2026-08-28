using System.Net;
using System.Text.Json;
using System.Web;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal session-history query endpoint consumed by the Operator API for the portal's
/// Reports section (Reports feature). Returns durable, secret-free terminal-outcome records
/// written by <see cref="ReportProgressFunction"/>, <see cref="CreateSessionFunction"/>, and
/// <see cref="Services.DeviceSessionLifecycleService"/> — independent of the live
/// "DeviceSessions" table's much shorter purge window.
///
/// GET /api/internal/session-history?from={iso8601}&amp;to={iso8601}
/// Defaults to the trailing 90 days when either bound is omitted.
/// </summary>
public sealed partial class SessionHistoryFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SessionHistoryRepository _historyRepo;
    private readonly ILogger<SessionHistoryFunction> _logger;

    public SessionHistoryFunction(SessionHistoryRepository historyRepo, ILogger<SessionHistoryFunction> logger)
    {
        _historyRepo = historyRepo;
        _logger = logger;
    }

    [Function(nameof(SessionHistoryFunction))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/session-history")] HttpRequestData req,
        FunctionContext context)
    {
        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var to = DateTimeOffset.TryParse(query.Get("to"), out var toParsed) ? toParsed : DateTimeOffset.UtcNow;
        var from = DateTimeOffset.TryParse(query.Get("from"), out var fromParsed) ? fromParsed : to - TimeSpan.FromDays(90);

        var records = new List<Contracts.Models.SessionHistoryRecord>();
        await foreach (var record in _historyRepo.QueryAsync(from, to, context.CancellationToken))
        {
            records.Add(record);
        }

        LogHistoryListed(_logger, from, to, records.Count);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(records, JsonOptions), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session history queried from {From} to {To}: {Count} records.")]
    private static partial void LogHistoryListed(ILogger logger, DateTimeOffset from, DateTimeOffset to, int count);
}
