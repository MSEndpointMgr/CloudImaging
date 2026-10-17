using System.Net;
using System.Text.Json;
using CloudImaging.Contracts.Models;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>Raised when an OS image's architecture does not match the sessions it would be assigned to.</summary>
public sealed class ArchitectureMismatchException(MachineArchitecture imageArchitecture, IReadOnlyList<(Guid SessionId, MachineArchitecture Architecture)> sessions)
    : InvalidOperationException(ArchitectureCompatibility.Describe(imageArchitecture, sessions))
{
    /// <summary>Architecture of the OS image that was attempted to be assigned.</summary>
    public MachineArchitecture ImageArchitecture { get; } = imageArchitecture;
    /// <summary>The sessions whose device architecture does not match <see cref="ImageArchitecture"/>.</summary>
    public IReadOnlyList<(Guid SessionId, MachineArchitecture Architecture)> Sessions { get; } = sessions;
}

/// <summary>Assignment-time OS image / device architecture enforcement (todo/arm64-support.md, Milestone 2).</summary>
public static class ArchitectureCompatibility
{
    /// <summary>Problem-details <c>type</c> URI for an architecture-mismatch 409 response.</summary>
    public const string ProblemType = "https://cloudimaging.io/errors/architecture-mismatch";

    /// <summary>Builds a human-readable message describing the mismatch between an image's architecture and the given sessions' device architectures.</summary>
    public static string Describe(MachineArchitecture imageArchitecture, IReadOnlyCollection<(Guid SessionId, MachineArchitecture Architecture)> sessions)
    {
        var deviceArchitectures = string.Join(" and ", sessions.Select(s => Display(s.Architecture)).Distinct());
        var devices = sessions.Count == 1 ? "The device is" : $"{sessions.Count} devices are";
        return $"This OS image is {Display(imageArchitecture)}, but {devices} {deviceArchitectures}. Assign an image built for the device's architecture.";
    }

    /// <summary>Writes a 409 ProblemDetails naming the image and each incompatible session's architecture.</summary>
    public static async Task<HttpResponseData> ConflictAsync(HttpRequestData req, ArchitectureMismatchException mismatch, CancellationToken ct)
    {
        var response = req.CreateResponse(HttpStatusCode.Conflict);
        response.Headers.Add("Content-Type", "application/problem+json");
        await response.WriteStringAsync(JsonSerializer.Serialize(new
        {
            type = ProblemType,
            title = "OS image architecture does not match the device.",
            status = 409,
            detail = mismatch.Message,
            imageArchitecture = MachineArchitecturePlatform.Slug(mismatch.ImageArchitecture),
            sessions = mismatch.Sessions.Select(s => new { sessionId = s.SessionId, architecture = MachineArchitecturePlatform.Slug(s.Architecture) }),
        }), ct);
        return response;
    }

    private static string Display(MachineArchitecture architecture) => architecture == MachineArchitecture.Arm64 ? "ARM64" : "x64";
}
