using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Internal per-portal-user location preference endpoints consumed by the Operator API
/// (Location Labels feature). userId is the caller's Entra object id (oid) — neither this API
/// nor the Operator API otherwise has any concept of "the signed-in portal user", so it is
/// passed explicitly as a route parameter, the same way sessionId/imageId are elsewhere.
///
/// GET /api/internal/user-preferences/{userId} — returns the user's preferred location (or 404)
/// PUT /api/internal/user-preferences/{userId} — sets/clears the user's preferred location
/// </summary>
public sealed partial class UserPreferencesFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly UserLocationPreferenceRepository _repo;
    private readonly ILogger<UserPreferencesFunctions> _logger;

    public UserPreferencesFunctions(UserLocationPreferenceRepository repo, ILogger<UserPreferencesFunctions> logger)
    {
        _repo = repo;
        _logger = logger;
    }

    [Function("GetUserLocationPreference")]
    public async Task<HttpResponseData> GetUserLocationPreference(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/user-preferences/{userId}")] HttpRequestData req,
        string userId,
        FunctionContext context)
    {
        var preference = await _repo.GetAsync(userId, context.CancellationToken);
        if (preference is null)
        {
            return req.CreateResponse(HttpStatusCode.NotFound);
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(preference, JsonOptions), context.CancellationToken);
        return response;
    }

    [Function("PutUserLocationPreference")]
    public async Task<HttpResponseData> PutUserLocationPreference(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/user-preferences/{userId}")] HttpRequestData req,
        string userId,
        FunctionContext context)
    {
        UserLocationPreference? body;
        try
        {
            body = await JsonSerializer.DeserializeAsync<UserLocationPreference>(req.Body, JsonOptions, context.CancellationToken);
        }
        catch (JsonException) { return req.CreateResponse(HttpStatusCode.BadRequest); }

        var toSave = new UserLocationPreference
        {
            UserId = userId,
            LocationId = body?.LocationId,
            LocationName = body?.LocationName,
        };

        var saved = await _repo.UpsertAsync(toSave, context.CancellationToken);
        LogSaved(_logger, userId, saved.LocationId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(saved, JsonOptions), context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Location preference saved for user {UserId} (locationId={LocationId}).")]
    private static partial void LogSaved(ILogger logger, string userId, Guid? locationId);
}
