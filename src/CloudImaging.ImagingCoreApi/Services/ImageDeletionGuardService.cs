using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Repositories;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Guards OS image deletion by checking for active imaging sessions assigned to the image (T084, FR-037).
/// An image with an active session (SessionStarted or SessionInProgress) cannot be deleted.
/// </summary>
public sealed partial class ImageDeletionGuardService
{
    private readonly DeviceSessionRepository _sessionRepo;
    private readonly ILogger<ImageDeletionGuardService> _logger;

    public ImageDeletionGuardService(
        DeviceSessionRepository sessionRepo,
        ILogger<ImageDeletionGuardService> logger)
    {
        _sessionRepo = sessionRepo;
        _logger = logger;
    }

    /// <summary>
    /// Returns true if the image is safe to delete (no active sessions referencing it).
    /// Returns false if any session in a non-terminal state has this image assigned.
    /// </summary>
    public async Task<bool> CanDeleteAsync(Guid imageId, CancellationToken ct = default)
    {
        await foreach (var session in _sessionRepo.QueryActiveAsync(ct))
        {
            if (session.AssignedOsImageId == imageId
                && session.State is SessionState.SessionStarted
                              or SessionState.SessionInProgress)
            {
                LogDeleteBlocked(_logger, imageId, session.SessionId, session.State);
                return false;
            }
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Delete blocked: image {ImageId} is referenced by active session {SessionId} (state={State}).")]
    private static partial void LogDeleteBlocked(
        ILogger logger, Guid imageId, Guid sessionId, SessionState state);
}
