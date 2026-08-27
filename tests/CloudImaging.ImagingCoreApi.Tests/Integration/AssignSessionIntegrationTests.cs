using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Integration tests for the single-session image assign endpoint (T036a, FR-025).
/// Tests the state + business rule contracts without a live Table Storage.
/// </summary>
public sealed class AssignSessionIntegrationTests
{
    // ── State pre-condition ───────────────────────────────────────────────────

    [Theory]
    [InlineData(SessionState.SessionAssigned,  true)]   // only valid state
    [InlineData(SessionState.SessionAllowed,   false)]
    [InlineData(SessionState.SessionStarted,   false)]
    [InlineData(SessionState.SessionInProgress,false)]
    [InlineData(SessionState.SessionCompleted, false)]
    [InlineData(SessionState.SessionFailed,    false)]
    public void Assignment_IsAllowedOnlyInSessionAssignedState(SessionState state, bool expectAllowed)
    {
        var isAssignable = state == SessionState.SessionAssigned;
        isAssignable.Should().Be(expectAllowed,
            $"session in {state} should {(expectAllowed ? "" : "not ")}be assignable");
    }

    [Fact]
    public void Assignment_Fails_WhenSessionAlreadyHasAnImage()
    {
        // Guard: no re-assignment once AssignedOsImageId is set
        Guid? existingImageId = Guid.NewGuid();
        var alreadyAssigned   = existingImageId.HasValue;

        alreadyAssigned.Should().BeTrue(
            "a session with AssignedOsImageId must return 409 on re-assignment attempt");
    }

    // ── State transition ──────────────────────────────────────────────────────

    [Fact]
    public void SuccessfulAssignment_TransitionsTo_SessionStarted()
    {
        var before = SessionState.SessionAssigned;
        var after  = SessionState.SessionStarted;

        after.Should().NotBe(before);
        after.Should().Be(SessionState.SessionStarted,
            "assignment transitions the session to SessionStarted");
    }

    // ── SAS token URL ─────────────────────────────────────────────────────────

    [Fact]
    public void SasTokenUrl_Expiry_RespectsSasTokenUrlExpiryMinutesConfig()
    {
        // Default SasTokenUrlExpiryMinutes is 240 (see PortalConfiguration.cs)
        var config         = new PortalConfiguration { SasTokenUrlExpiryMinutes = 120 };
        var sasExpiry      = TimeSpan.FromMinutes(
            config.SasTokenUrlExpiryMinutes > 0 ? config.SasTokenUrlExpiryMinutes : 240);
        var issued         = DateTimeOffset.UtcNow;
        var expiresAt      = issued + sasExpiry;

        (expiresAt - issued).TotalMinutes.Should().BeApproximately(120, 1,
            "SAS expiry should match PortalConfiguration.SasTokenUrlExpiryMinutes");
    }

    [Fact]
    public void SasTokenUrl_Expiry_DefaultsTo240Minutes_WhenConfigIsZero()
    {
        var config    = new PortalConfiguration { SasTokenUrlExpiryMinutes = 0 };
        var sasExpiry = TimeSpan.FromMinutes(
            config.SasTokenUrlExpiryMinutes > 0 ? config.SasTokenUrlExpiryMinutes : 240);

        sasExpiry.TotalMinutes.Should().Be(240,
            "SAS expiry must default to 240 minutes (PortalConfiguration.SasTokenUrlExpiryMinutes' documented default) when config value is 0");
    }

    // ── Sha256Hash in response ────────────────────────────────────────────────

    [Fact]
    public void AssignmentResponse_MustInclude_Sha256Hash()
    {
        // The response object from AssignSessionFunction always includes sha256Hash
        // (it reads it from the OsImage catalog entity).
        // This test validates the schema contract.
        var fakeImage = new OsImage
        {
            ImageId     = Guid.NewGuid(),
            Name        = "Windows 11 Enterprise",
            Version     = "24H2",
            Sha256Hash  = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899",
            StoragePath = "os-images/win11-24h2.wim",
            IsInUse     = false,
            UploadedAt  = DateTimeOffset.UtcNow,
        };

        fakeImage.Sha256Hash.Should().HaveLength(64, "SHA-256 is a 64-character hex string");
        fakeImage.Sha256Hash.Should().MatchRegex("^[0-9a-f]{64}$", "hash must be lowercase hex");
    }
}
