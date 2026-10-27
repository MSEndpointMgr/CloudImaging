using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// POST internal/drivers/assignment-preview: the Driver set column on the Devices page. Per
/// assignable session the model it matches for the selected image, or why none applies, and the
/// models anyone starting imaging can choose instead. Reads only; nothing is written.
/// </summary>
public sealed class DriverAssignmentFunctions
{
    /// <summary>Most sessions one preview covers.</summary>
    public const int MaxSessions = 200;

    private readonly DeviceSessionRepository _sessions;
    private readonly OsImageRepository _images;
    private readonly DriverSetResolver _resolver;
    private readonly DriverSetDocumentRepository _sets;

    /// <param name="sessions">Sessions.</param>
    /// <param name="images">OS images.</param>
    /// <param name="resolver">Matches devices to models.</param>
    /// <param name="sets">Set documents, for each option's size.</param>
    public DriverAssignmentFunctions(DeviceSessionRepository sessions, OsImageRepository images, DriverSetResolver resolver, DriverSetDocumentRepository sets)
    {
        _sessions = sessions;
        _images = images;
        _resolver = resolver;
        _sets = sets;
    }

    /// <summary>POST internal/drivers/assignment-preview.</summary>
    [Function("PreviewDriverAssignment")]
    public async Task<HttpResponseData> PreviewDriverAssignment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/assignment-preview")] HttpRequestData req,
        FunctionContext context)
    {
        var ct = context.CancellationToken;
        var request = await DriverHttp.ReadAsync<DriverAssignmentPreviewRequest>(req, ct);
        if (request is null)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "The request body is not valid.", ct);
        }

        if (request.SessionIds.Count > MaxSessions)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, $"Preview at most {MaxSessions} devices at once.", ct);
        }

        var image = await _images.GetByIdAsync(request.OsImageId, ct);
        if (image is null)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.NotFound, "OS image not found.", ct);
        }

        var sessions = new List<DriverAssignmentPreviewSession>();
        foreach (var sessionId in request.SessionIds.Distinct())
        {
            var session = await _sessions.GetByIdAsync(sessionId, ct);
            if (session is null || session.State != SessionState.SessionAssigned || session.AssignedOsImageId.HasValue)
            {
                continue;
            }

            var pin = await _resolver.ResolveAsync(session, image, ct);
            sessions.Add(new DriverAssignmentPreviewSession
            {
                SessionId = sessionId,
                MatchedModelId = pin.SkipReason is null ? pin.ModelId : null,
                SkipReason = pin.SkipReason,
                MatchKey = pin.MatchKey,
            });
        }

        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, new DriverAssignmentPreview
        {
            Release = image.WindowsRelease,
            Architecture = image.Architecture,
            Options = await _resolver.OptionsAsync(image, _sets, ct),
            Sessions = sessions,
        }, ct);
    }
}
