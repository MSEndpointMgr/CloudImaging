using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Services;

namespace CloudImaging.ImagingCoreApi.Domain;

/// <summary>
/// Factory for creating new DeviceSession instances with correctly initialised defaults (FR-021).
/// </summary>
public static class DeviceSessionFactory
{
    /// <summary>
    /// Creates a new session in the <see cref="SessionState.SessionInit"/> state.
    /// Passcode is generated and hashed; device-session token is issued separately
    /// by the calling service after pre-flight evaluation completes.
    /// </summary>
    /// <param name="registration">Registration payload the device submitted.</param>
    public static (DeviceSession Session, string PlainPasscode) CreateNew(
        DeviceRegistrationPayload registration)
    {
        var sessionId = Guid.NewGuid();
        var plainPasscode = PasscodeSecurityPolicy.GeneratePasscode();
        var passcodeHash = PasscodeSecurityPolicy.HashPasscode(plainPasscode);
        var now = DateTimeOffset.UtcNow;

        var session = new DeviceSession
        {
            SessionId = sessionId,
            State = SessionState.SessionInit,
            DeviceSerialNumber = registration.SerialNumber,
            DeviceManufacturer = registration.Manufacturer,
            DeviceModel = registration.Model,
            MacAddress = registration.MacAddress,
            HardwareMetadata = registration.Hardware,
            LocationId = registration.LocationId,
            LocationName = registration.LocationName,
            Architecture = registration.Architecture ?? MachineArchitecture.X64,
            SecurityPosture = registration.SecurityPosture,
            PreFlightAuthorizationResult = PreFlightAuthorizationResult.Skipped,
            Passcode = passcodeHash,
            // Informational only: the authoritative deadline is heartbeat silence, which slides
            // every time the device polls. This records when an idle device would be expired.
            PasscodeExpiresAt = now + DeviceSessionLifecycleService.InactivityTimeout,
            PasscodeConsumed = false,
            OverallProgressPercent = 0,
            CreatedAt = now,
            LastHeartbeatAt = now,
            // Terminal purge: 24 hours after reaching a terminal state (FR-021)
            PurgeAt = null
        };

        return (session, plainPasscode);
    }
}
