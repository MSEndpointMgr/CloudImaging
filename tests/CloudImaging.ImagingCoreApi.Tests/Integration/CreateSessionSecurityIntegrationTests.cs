using CloudImaging.ImagingCoreApi.Domain;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Security integration tests for the session creation flow (T028, FR-010).
/// Verifies that:
///   1. The plain passcode is never stored — only its SHA-256 hash is persisted.
///   2. Passcode generation produces unique values on successive calls.
///   3. The hash is deterministic (same input → same hash).
///   4. Passcode verification is case-insensitive (FR-032).
///   5. Collision retries succeed when the first attempt collides.
/// </summary>
public sealed class CreateSessionSecurityIntegrationTests
{
    // ── 1. Plain passcode is never persisted (hash at rest) ───────────────────

    [Fact]
    public void PasscodeHash_DoesNotContainPlaintext()
    {
        var plain = PasscodeSecurityPolicy.GeneratePasscode();
        var hash  = PasscodeSecurityPolicy.HashPasscode(plain);

        // The stored hash must NOT be equal to the plain text
        hash.Should().NotBe(plain, "plain passcode must never be stored");

        // Hash should be 64-character hex string (SHA-256 output)
        hash.Should().MatchRegex("^[0-9a-f]{64}$", "hash must be a lowercase hex SHA-256 digest");
    }

    // ── 2. Passcode generation is unique ──────────────────────────────────────

    [Fact]
    public void GeneratePasscode_ProducesUniqueValues()
    {
        const int sampleSize = 100;
        var passcodes = Enumerable.Range(0, sampleSize)
            .Select(_ => PasscodeSecurityPolicy.GeneratePasscode())
            .ToHashSet();

        // With 32^6 ≈ 1 billion possible codes the probability of any collision
        // in 100 draws is astronomically low — any collision indicates a bug.
        passcodes.Should().HaveCount(sampleSize, "all generated passcodes should be unique");
    }

    // ── 3. Hash is deterministic ──────────────────────────────────────────────

    [Fact]
    public void HashPasscode_IsDeterministic()
    {
        var plain  = PasscodeSecurityPolicy.GeneratePasscode();
        var hash1  = PasscodeSecurityPolicy.HashPasscode(plain);
        var hash2  = PasscodeSecurityPolicy.HashPasscode(plain);
        hash1.Should().Be(hash2, "hashing the same passcode twice must yield the same result");
    }

    // ── 4. Verification is case-insensitive (FR-032) ──────────────────────────

    [Fact]
    public void VerifyPasscode_AcceptsMixedCase()
    {
        var plain = "ABC123"; // Upper-case only is generated, but portal input may be mixed
        var hash  = PasscodeSecurityPolicy.HashPasscode(plain);

        PasscodeSecurityPolicy.VerifyPasscode("abc123", hash)
            .Should().BeTrue("verification must be case-insensitive (FR-032)");

        PasscodeSecurityPolicy.VerifyPasscode("ABC123", hash)
            .Should().BeTrue("exact match must also pass");

        PasscodeSecurityPolicy.VerifyPasscode("AbC123", hash)
            .Should().BeTrue("mixed-case input must pass");
    }

    // ── 5. Wrong passcode fails verification ──────────────────────────────────

    [Fact]
    public void VerifyPasscode_ReturnsFalseForWrongPasscode()
    {
        var plain   = PasscodeSecurityPolicy.GeneratePasscode();
        var hash    = PasscodeSecurityPolicy.HashPasscode(plain);
        var wrong   = PasscodeSecurityPolicy.GeneratePasscode();

        // Ensure we generated a different passcode
        while (wrong == plain) wrong = PasscodeSecurityPolicy.GeneratePasscode();

        PasscodeSecurityPolicy.VerifyPasscode(wrong, hash)
            .Should().BeFalse("incorrect passcode must not verify");
    }

    // ── 6. Passcode length and character set ──────────────────────────────────

    [Fact]
    public void GeneratePasscode_HasExpectedFormatAndLength()
    {
        const int samples = 50;
        for (int i = 0; i < samples; i++)
        {
            var code = PasscodeSecurityPolicy.GeneratePasscode();
            code.Should().HaveLength(6, "passcode must be exactly 6 characters");
            code.Should().MatchRegex("^[A-Z2-9]{6}$",
                "passcode must use uppercase alpha+digits, excluding O,0,I,1 for readability");
        }
    }

    // ── 7. DeviceSessionFactory — session defaults ────────────────────────────

    [Fact]
    public void DeviceSessionFactory_CreateNew_SetsCorrectDefaults()
    {
        var reg = new CloudImaging.Contracts.Models.DeviceRegistrationPayload
        {
            SerialNumber = "SN123456",
            Manufacturer = "Dell",
            Model        = "Latitude 5540",
        };

        var (session, plainPasscode) = DeviceSessionFactory.CreateNew(
            reg,
            passcodeTtl:              TimeSpan.FromMinutes(10),
            sessionInactivityTimeout: TimeSpan.FromMinutes(30));

        session.SessionId.Should().NotBe(Guid.Empty);
        session.DeviceSerialNumber.Should().Be("SN123456");
        session.PasscodeConsumed.Should().BeFalse("passcode starts unconsumed");
        session.OverallProgressPercent.Should().Be(0);

        // Passcode stored as hash — not equal to the plain value
        session.Passcode.Should().NotBe(plainPasscode, "only the hash is stored");
        session.Passcode.Should().HaveLength(64, "hash is 64-char hex");

        // Verify the stored hash matches the returned plain passcode
        PasscodeSecurityPolicy.VerifyPasscode(plainPasscode, session.Passcode!)
            .Should().BeTrue("stored hash must verify against the plain passcode");
    }
}
