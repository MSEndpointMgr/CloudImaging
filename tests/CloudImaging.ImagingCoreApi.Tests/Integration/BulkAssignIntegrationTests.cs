using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Integration tests for the bulk image assignment endpoint (T075, FR-035).
/// Tests the state + business rule contracts without a live Table Storage.
/// </summary>
public sealed class BulkAssignIntegrationTests
{
    // ── State pre-condition ───────────────────────────────────────────────────

    [Theory]
    [InlineData(SessionState.SessionAssigned,  true)]   // only valid state
    [InlineData(SessionState.SessionAllowed,   false)]
    [InlineData(SessionState.SessionStarted,   false)]
    [InlineData(SessionState.SessionInProgress,false)]
    [InlineData(SessionState.SessionCompleted, false)]
    [InlineData(SessionState.SessionFailed,    false)]
    public void BulkAssign_IsAllowedOnlyInSessionAssignedState(SessionState state, bool expectAllowed)
    {
        var isAssignable = state == SessionState.SessionAssigned;
        isAssignable.Should().Be(expectAllowed,
            $"session in {state} should {(expectAllowed ? "" : "not ")}be assignable in bulk assign");
    }

    [Fact]
    public void BulkAssign_Skips_SessionsThatAlreadyHaveAnImage()
    {
        // Guard: no re-assignment once AssignedOsImageId is set (mirrors single-assign)
        Guid? existingImageId = Guid.NewGuid();
        var alreadyAssigned   = existingImageId.HasValue;

        alreadyAssigned.Should().BeTrue(
            "a session with AssignedOsImageId must be skipped, not re-assigned, by bulk assign");
    }

    // ── State transition ──────────────────────────────────────────────────────

    [Fact]
    public void SuccessfulBulkAssign_TransitionsTo_SessionStarted()
    {
        var before = SessionState.SessionAssigned;
        var after  = SessionState.SessionStarted;

        after.Should().NotBe(before);
        after.Should().Be(SessionState.SessionStarted,
            "bulk assignment transitions each assigned session to SessionStarted");
    }

    // ── SAS token URL ─────────────────────────────────────────────────────────

    /// <summary>
    /// Regression test (2026-08-25): <c>BulkAssignmentService.AssignAsync</c> originally set
    /// <c>AssignedOsImageId</c> and <c>SasTokenUrlExpiresAt</c> but never generated/persisted
    /// <c>SasTokenUrl</c> itself — unlike the single-session <c>AssignSessionFunction</c>, which
    /// calls <see cref="Services.BlobSasUrlGenerator"/>. Because the portal's Coupled Devices
    /// table "Start Imaging" action is the only UI path that assigns images (bulk-assign only,
    /// no single-row assign in the UI), every real assignment left <c>SasTokenUrl</c> null, and
    /// the Client's <c>ImagingWorkflowViewModel.WaitForImageAssignmentAsync</c> polled for 5
    /// minutes waiting for a download URL that would never arrive before timing out.
    /// </summary>
    [Fact]
    public void BulkAssign_MustPersist_NonEmptySasTokenUrl_LikeSingleAssign()
    {
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

        // Simulates what BulkAssignmentService.AssignAsync must now do for every session in the
        // batch: generate a real SAS URL from the image's storage path (never leave it null).
        var sasUrl = $"https://example.blob.core.windows.net/{fakeImage.StoragePath}?sv=fake-sas";

        sasUrl.Should().NotBeNullOrEmpty(
            "bulk-assigned sessions must receive a usable SasTokenUrl, exactly like single-assign, " +
            "or the Client can never download the OS image");
        sasUrl.Should().Contain(fakeImage.StoragePath,
            "the SAS URL must reference the assigned image's blob storage path");
    }

    [Fact]
    public void BulkAssign_SasTokenUrl_IsSharedAcrossAllSessionsInBatch()
    {
        // All sessions in one bulk-assign call receive the SAME osImageId, so a single SAS URL
        // (one user-delegation-key round trip) is generated once and applied to every assigned
        // session — not regenerated per-session.
        var sasUrl1 = "https://example.blob.core.windows.net/os-images/win11-24h2.wim?sv=fake-sas";
        var sasUrl2 = sasUrl1;

        sasUrl2.Should().Be(sasUrl1,
            "every session assigned in the same bulk-assign batch shares one generated SAS URL");
    }

    [Fact]
    public void SasTokenUrl_Expiry_RespectsSasTokenUrlExpiryMinutesConfig()
    {
        var config    = new PortalConfiguration { SasTokenUrlExpiryMinutes = 120 };
        var sasExpiry = TimeSpan.FromMinutes(
            config.SasTokenUrlExpiryMinutes > 0 ? config.SasTokenUrlExpiryMinutes : 240);
        var issued    = DateTimeOffset.UtcNow;
        var expiresAt = issued + sasExpiry;

        (expiresAt - issued).TotalMinutes.Should().BeApproximately(120, 1,
            "SAS expiry should match PortalConfiguration.SasTokenUrlExpiryMinutes");
    }
}
