using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;

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
    public static (DeviceSession Session, string PlainPasscode) CreateNew(
        DeviceRegistrationPayload registration,
        TimeSpan passcodeTtl,
        TimeSpan sessionInactivityTimeout)
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
            HardwareMetadata = registration.Hardware,
            PreFlightAuthorizationResult = PreFlightAuthorizationResult.Skipped,
            Passcode = passcodeHash,
            PasscodeExpiresAt = now + passcodeTtl,
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
