using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>Raised when a manual driver choice cannot be used; nothing is assigned.</summary>
public sealed class DriverChoiceConflictException(IReadOnlyList<DriverChoiceError> errors) : Exception(DriverChoices.Describe(errors.Count))
{
    /// <summary>Why each rejected choice cannot be used.</summary>
    public IReadOnlyList<DriverChoiceError> Errors { get; } = errors;
}

/// <summary>
/// Manual driver choices sent when imaging starts (operator-and-core-api section 4): the bulk
/// <c>driverChoices</c> list or the single <c>driverChoice</c>, and the actor required with them.
/// </summary>
public static class DriverChoices
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>"The chosen driver model is no longer available for 2 devices. Choose again."</summary>
    public static string Describe(int count) =>
        $"The chosen driver model is no longer available for {(count == 1 ? "1 device" : $"{count} devices")}. Choose again.";

    /// <summary>
    /// Reads the choices of a request. Returns an error for a choice of a session the request does
    /// not assign, two choices for one session, a <c>Model</c> choice without <c>modelId</c>, an
    /// unknown kind, or choices without an acting user.
    /// </summary>
    public static string? TryRead(JsonElement root, IReadOnlyCollection<Guid> sessionIds, out IReadOnlyList<DriverChoice> choices, out string? chosenBy)
    {
        choices = [];
        chosenBy = null;
        var list = new List<DriverChoice>();
        var single = sessionIds.Count == 1 && root.TryGetProperty("driverChoice", out var one) && one.ValueKind == JsonValueKind.Object;
        if (single)
        {
            if (Read(root.GetProperty("driverChoice"), sessionIds.First()) is not { } choice)
            {
                return "The driver choice is not valid.";
            }

            list.Add(choice);
        }
        else if (root.TryGetProperty("driverChoices", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in many.EnumerateArray())
            {
                var sessionId = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("sessionId", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed) ? parsed : Guid.Empty;
                if (sessionId == Guid.Empty || Read(element, sessionId) is not { } choice)
                {
                    return "A driver choice is not valid.";
                }

                if (!sessionIds.Contains(sessionId))
                {
                    return "A driver choice names a device that is not being assigned.";
                }

                if (list.Any(c => c.SessionId == sessionId))
                {
                    return "A device has more than one driver choice.";
                }

                list.Add(choice);
            }
        }

        if (list.Count == 0)
        {
            return null;
        }

        chosenBy = root.TryGetProperty("actor", out var actor) && actor.ValueKind == JsonValueKind.Object && actor.TryGetProperty("upn", out var upn) && upn.ValueKind == JsonValueKind.String ? upn.GetString() : null;
        if (string.IsNullOrWhiteSpace(chosenBy))
        {
            return DriverHttpText.MissingActor;
        }

        choices = list;
        return null;
    }

    /// <summary>409 with the detail and the reason per session.</summary>
    public static async Task<HttpResponseData> ConflictAsync(HttpRequestData req, DriverChoiceConflictException conflict, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(conflict);
        var response = req.CreateResponse(HttpStatusCode.Conflict);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            type = "https://cloudimaging.io/errors/drivers",
            title = "Conflict",
            status = 409,
            detail = conflict.Message,
            errors = conflict.Errors,
        }, Web), ct);
        return response;
    }

    private static DriverChoice? Read(JsonElement element, Guid sessionId)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("kind", out var kindProperty)
            || kindProperty.ValueKind != JsonValueKind.String
            || !Enum.TryParse<DriverChoiceKind>(kindProperty.GetString(), ignoreCase: false, out var kind)
            || !Enum.IsDefined(kind))
        {
            return null;
        }

        Guid? modelId = element.TryGetProperty("modelId", out var model) && model.ValueKind == JsonValueKind.String && Guid.TryParse(model.GetString(), out var parsed) ? parsed : null;
        if (kind == DriverChoiceKind.Model && modelId is null)
        {
            return null;
        }

        return new DriverChoice { SessionId = sessionId, Kind = kind, ModelId = kind == DriverChoiceKind.Model ? modelId : null };
    }
}
