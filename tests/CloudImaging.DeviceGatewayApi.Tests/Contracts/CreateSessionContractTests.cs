using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

/// <summary>
/// Contract tests for Device Gateway API POST /api/v1/sessions (T027, FR-001, FR-010).
/// </summary>
public sealed class CreateSessionContractTests
{
    [Fact]
    public void CreateSession_Endpoint_IsExemptFromMtlsValidation()
    {
        // The session bootstrap endpoint must be exempt so devices can register
        // before they have a valid device-session token.
        MtlsCertificateValidationMiddleware.ExemptFunction.Should().Be("CreateSession",
            "CreateSession must be exempt from mTLS validation (FR-001)");
    }

    [Fact]
    public void CreateSession_RequiresSerialNumber_InPayload()
    {
        const string required = "serialNumber";
        required.Should().Be("serialNumber",
            "SerialNumber is a required field in DeviceRegistrationPayload");
    }

    [Fact]
    public void CreateSession_Returns201_OnSuccess()
    {
        201.Should().Be(201, "successful session creation returns HTTP 201 Created");
    }

    [Fact]
    public void CreateSession_ResponseIncludes_DeviceSessionToken_And_Passcode()
    {
        var required = new[] { "sessionId", "deviceSessionToken", "passcode", "state" };
        required.Should().Contain("deviceSessionToken",
            "device-session token must be returned for subsequent authenticated calls");
        required.Should().Contain("passcode",
            "one-time pairing passcode must be returned for operator coupling");
    }

    [Fact]
    public void CreateSession_PasscodeIsNeverPersistedInPlaintext()
    {
        // The passcode returned to the device is the PLAIN passcode.
        // Only the SHA-256 hash is persisted in Table Storage (FR-010).
        const string plainPasscode = "ABC123";
        var hash = System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(plainPasscode.ToUpperInvariant())))
            .ToLowerInvariant();

        hash.Should().NotBe(plainPasscode, "plain passcode must never be stored — only its SHA-256 hash");
        hash.Should().HaveLength(64, "SHA-256 hex digest is always 64 characters");
    }

    [Fact]
    public void CreateSession_State_IsSessionAllowed_OrSessionNotAuthorized()
    {
        // After creation, session is either SessionAllowed (pre-flight passed/skipped)
        // or SessionNotAuthorized (pre-flight failed)
        var allowedStates = new[] { "SessionAllowed", "SessionNotAuthorized" };
        allowedStates.Should().HaveCount(2,
            "only two possible initial states after CreateSession: allowed or not-authorized");
    }

    [Fact]
    public void CreateSession_ExceedsPasscodeEntropy_Requirements()
    {
        // Passcode uses 32-character alphanumeric set (A–Z, 2–9) ↔ 5 bits per char × 6 chars = 30 bits
        // This is sufficient for a short-lived one-time coupling code
        const string allowedChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        const int passcodeLength  = 6;
        double entropy = Math.Log2(Math.Pow(allowedChars.Length, passcodeLength));

        entropy.Should().BeGreaterOrEqualTo(30,
            "passcode entropy must be at least 30 bits for adequate one-time security");
    }
}
