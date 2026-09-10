using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Repositories;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Repositories;

/// <summary>
/// Round-trip tests for the DeviceSession Table Storage mapping.
///
/// These exist because hardware metadata (FR-001a) was collected by the Client, accepted by the
/// Device Gateway, set on the domain model and copied through every state transition — but never
/// written by ToEntity, so it became null on the first persistence round-trip. Nothing covered the
/// mapping, so nothing failed. Any new DeviceSession property must be asserted here.
/// </summary>
public sealed class DeviceSessionEntityMappingTests
{
    private static DeviceSession FullyPopulatedSession() => new()
    {
        SessionId = Guid.Parse("8f14e45f-ceea-467a-9f4c-8b1d2e3a4b5c"),
        State = SessionState.SessionInProgress,
        DeviceSerialNumber = "JHK4L92",
        DeviceManufacturer = "Dell Inc.",
        DeviceModel = "Latitude 5450",
        MacAddress = "A4B1C2D3E4F5",
        HardwareMetadata = new DeviceHardwareMetadata
        {
            MotherboardManufacturer = "Dell Inc.",
            MotherboardModel = "0ABCD1",
            BiosVersion = "1.14.0",
            NicIdentifiers = ["Ethernet|A4B1C2D3E4F5", "Wi-Fi|B5C2D3E4F6A7"],
            StorageLayout = ["NVMe KBG50ZNV512G|512110190592"],
        },
        LocationId = Guid.Parse("1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed"),
        LocationName = "Copenhagen HQ",
        PreFlightAuthorizationResult = PreFlightAuthorizationResult.MatchedAutopilotV1,
        Passcode = "hashed-passcode",
        PasscodeExpiresAt = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero),
        PasscodeConsumed = true,
        DeviceSessionToken = "opaque-token",
        DeviceSessionTokenExpiresAt = new DateTimeOffset(2026, 9, 10, 14, 0, 0, TimeSpan.Zero),
        AssignedOsImageId = Guid.Parse("2c3d4e5f-6a7b-4c8d-9e0f-1a2b3c4d5e6f"),
        SasTokenUrl = "https://example.blob.core.windows.net/images/win11.wim?sig=redacted",
        SasTokenUrlExpiresAt = new DateTimeOffset(2026, 9, 10, 16, 0, 0, TimeSpan.Zero),
        PartitioningSchemeSnapshotJson = """{"scheme":"gpt"}""",
        OverallProgressPercent = 42,
        CurrentStep = "ApplyImage",
        CreatedAt = new DateTimeOffset(2026, 9, 10, 11, 0, 0, TimeSpan.Zero),
        LastHeartbeatAt = new DateTimeOffset(2026, 9, 10, 11, 30, 0, TimeSpan.Zero),
        TerminalAt = new DateTimeOffset(2026, 9, 10, 13, 0, 0, TimeSpan.Zero),
        PurgeAt = new DateTimeOffset(2026, 9, 11, 13, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void RoundTrip_PreservesEveryPersistedField()
    {
        var original = FullyPopulatedSession();

        var restored = DeviceSessionRepository.FromEntity(
            DeviceSessionRepository.ToEntity(original, "active"));

        // DeviceSession is a record, so this compares every property by value. A newly added
        // property that ToEntity/FromEntity forgets will fail here.
        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void RoundTrip_PreservesHardwareMetadata()
    {
        var original = FullyPopulatedSession();

        var restored = DeviceSessionRepository.FromEntity(
            DeviceSessionRepository.ToEntity(original, "active"));

        restored.HardwareMetadata.Should().NotBeNull();
        restored.HardwareMetadata!.BiosVersion.Should().Be("1.14.0");
        restored.HardwareMetadata.MotherboardManufacturer.Should().Be("Dell Inc.");
        restored.HardwareMetadata.NicIdentifiers.Should().HaveCount(2);
        restored.HardwareMetadata.StorageLayout.Should().ContainSingle();
        restored.MacAddress.Should().Be("A4B1C2D3E4F5");
    }

    [Fact]
    public void RoundTrip_PreservesLocation()
    {
        var original = FullyPopulatedSession();

        var restored = DeviceSessionRepository.FromEntity(
            DeviceSessionRepository.ToEntity(original, "active"));

        restored.LocationId.Should().Be(original.LocationId);
        restored.LocationName.Should().Be("Copenhagen HQ");
    }

    [Fact]
    public void RoundTrip_TolueratesSessionWithNoHardwareMetadata()
    {
        // WMI is not guaranteed to answer in WinPE, so the client may send no hardware block.
        var original = FullyPopulatedSession() with { HardwareMetadata = null, MacAddress = null };

        var restored = DeviceSessionRepository.FromEntity(
            DeviceSessionRepository.ToEntity(original, "active"));

        restored.HardwareMetadata.Should().BeNull();
        restored.MacAddress.Should().BeNull();
    }

    [Fact]
    public void FromEntity_DegradesToNull_WhenHardwareJsonIsCorrupt()
    {
        // A malformed blob must not make an otherwise-healthy session unreadable.
        var entity = DeviceSessionRepository.ToEntity(FullyPopulatedSession(), "active");
        entity["HardwareMetadataJson"] = "{not-valid-json";

        var restored = DeviceSessionRepository.FromEntity(entity);

        restored.HardwareMetadata.Should().BeNull();
        restored.DeviceSerialNumber.Should().Be("JHK4L92");
    }
}

/// <summary>
/// Guards the state-transition bug class directly: transitions are written as
/// <c>session with { ... }</c> so unlisted properties are carried over automatically. Before that,
/// each transition re-listed ~25 properties by hand and several were being dropped.
/// </summary>
public sealed class DeviceSessionTransitionTests
{
    [Fact]
    public void WithExpression_CarriesForwardEverythingNotExplicitlyChanged()
    {
        var session = new DeviceSession
        {
            SessionId = Guid.NewGuid(),
            State = SessionState.SessionAllowed,
            DeviceSerialNumber = "JHK4L92",
            DeviceManufacturer = "Dell Inc.",
            DeviceModel = "Latitude 5450",
            MacAddress = "A4B1C2D3E4F5",
            HardwareMetadata = new DeviceHardwareMetadata { BiosVersion = "1.14.0" },
            LocationId = Guid.NewGuid(),
            LocationName = "Copenhagen HQ",
            PartitioningSchemeSnapshotJson = """{"scheme":"gpt"}""",
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var coupled = session with
        {
            State = SessionState.SessionAssigned,
            PasscodeConsumed = true,
        };

        coupled.State.Should().Be(SessionState.SessionAssigned);
        coupled.PasscodeConsumed.Should().BeTrue();

        // The fields that used to be lost on transition.
        coupled.LocationId.Should().Be(session.LocationId);
        coupled.LocationName.Should().Be("Copenhagen HQ");
        coupled.HardwareMetadata.Should().BeSameAs(session.HardwareMetadata);
        coupled.MacAddress.Should().Be("A4B1C2D3E4F5");
        coupled.PartitioningSchemeSnapshotJson.Should().Be("""{"scheme":"gpt"}""");
        coupled.CreatedAt.Should().Be(session.CreatedAt);
    }
}
