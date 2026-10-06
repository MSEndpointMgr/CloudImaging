using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal CRUD for the admin-defined Autopilot group tags approvers choose from.
///
/// GET    /api/internal/autopilot/group-tags
/// POST   /api/internal/autopilot/group-tags
/// PUT    /api/internal/autopilot/group-tags/{id}
/// DELETE /api/internal/autopilot/group-tags/{id}
/// </summary>
public sealed partial class AutopilotGroupTagFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AutopilotGroupTagRepository _repo;
    private readonly ILogger<AutopilotGroupTagFunctions> _logger;

    public AutopilotGroupTagFunctions(AutopilotGroupTagRepository repo, ILogger<AutopilotGroupTagFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    [Function("ListAutopilotGroupTags")]
    public async Task<HttpResponseData> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/autopilot/group-tags")] HttpRequestData req,
        FunctionContext context)
    {
        var definitions = await _repo.ListAsync(context.CancellationToken);
        return await AutopilotRegistrationFunctions.JsonAsync(req, HttpStatusCode.OK, definitions, context.CancellationToken);
    }

    [Function("CreateAutopilotGroupTag")]
    public async Task<HttpResponseData> Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/autopilot/group-tags")] HttpRequestData req,
        FunctionContext context)
    {
        var (payload, error) = await ReadValidAsync(req, context.CancellationToken);
        if (error is not null)
        {
            return await AutopilotRegistrationFunctions.ProblemAsync(req, HttpStatusCode.BadRequest, error, context.CancellationToken);
        }

        var definition = new AutopilotGroupTagDefinition
        {
            Id = Guid.NewGuid(),
            Name = payload!.Name!.Trim(),
            Kind = payload.Kind!.Value,
            Value = payload.Value!.Trim(),
            Description = string.IsNullOrWhiteSpace(payload.Description) ? null : payload.Description.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _repo.AddAsync(definition, context.CancellationToken);
        LogChanged(_logger, definition.Id, "created");
        return await AutopilotRegistrationFunctions.JsonAsync(req, HttpStatusCode.Created, definition, context.CancellationToken);
    }

    [Function("UpdateAutopilotGroupTag")]
    public async Task<HttpResponseData> Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/autopilot/group-tags/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var definitionId))
        {
            return await AutopilotRegistrationFunctions.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid group tag id.", context.CancellationToken);
        }

        var (payload, error) = await ReadValidAsync(req, context.CancellationToken);
        if (error is not null)
        {
            return await AutopilotRegistrationFunctions.ProblemAsync(req, HttpStatusCode.BadRequest, error, context.CancellationToken);
        }

        var existing = await _repo.GetAsync(definitionId, context.CancellationToken);
        if (existing is null)
        {
            return await AutopilotRegistrationFunctions.ProblemAsync(req, HttpStatusCode.NotFound, "Group tag not found.", context.CancellationToken);
        }

        var updated = new AutopilotGroupTagDefinition
        {
            Id = definitionId,
            Name = payload!.Name!.Trim(),
            Kind = payload.Kind!.Value,
            Value = payload.Value!.Trim(),
            Description = string.IsNullOrWhiteSpace(payload.Description) ? null : payload.Description.Trim(),
            CreatedAt = existing.CreatedAt,
        };
        await _repo.UpdateAsync(updated, context.CancellationToken);
        LogChanged(_logger, definitionId, "updated");
        return await AutopilotRegistrationFunctions.JsonAsync(req, HttpStatusCode.OK, updated, context.CancellationToken);
    }

    [Function("DeleteAutopilotGroupTag")]
    public async Task<HttpResponseData> Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/autopilot/group-tags/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var definitionId))
        {
            return await AutopilotRegistrationFunctions.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid group tag id.", context.CancellationToken);
        }

        // Requests already approved keep the resolved tag string, so deleting a definition never rewrites history.
        await _repo.DeleteAsync(definitionId, context.CancellationToken);
        LogChanged(_logger, definitionId, "deleted");
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    private static async Task<(GroupTagPayload? Payload, string? Error)> ReadValidAsync(HttpRequestData req, CancellationToken ct)
    {
        GroupTagPayload? payload;
        try
        {
            payload = await JsonSerializer.DeserializeAsync<GroupTagPayload>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            return (null, "Invalid group tag payload.");
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Name) || payload.Name.Trim().Length > 64)
        {
            return (payload, "A name of up to 64 characters is required.");
        }

        if (payload.Kind is null)
        {
            return (payload, "Kind must be Static or Template.");
        }

        if (payload.Description is { Length: > 256 })
        {
            return (payload, "The description is limited to 256 characters.");
        }

        return (payload, AutopilotGroupTagTemplate.ValidateDefinition(payload.Kind.Value, payload.Value?.Trim()));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot group tag {DefinitionId} {Action}.")]
    private static partial void LogChanged(ILogger logger, Guid definitionId, string action);

    private sealed class GroupTagPayload
    {
        public string? Name { get; init; }
        public AutopilotGroupTagKind? Kind { get; init; }
        public string? Value { get; init; }
        public string? Description { get; init; }
    }
}
