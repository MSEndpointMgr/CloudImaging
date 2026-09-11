using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Unit;

/// <summary>
/// Regression tests for the JSON shape of <see cref="SessionHistoryRecord"/>, which the
/// session-history endpoint serializes directly rather than through a hand-built DTO.
///
/// Every other session endpoint writes its enums with <c>.ToString()</c>, so the portal matches
/// <c>finalState</c> against names such as "SessionCompleted". <see cref="JsonSerializerDefaults.Web"/>
/// does not add a string enum converter, so without <c>[JsonConverter(typeof(JsonStringEnumConverter))]</c>
/// on the enums themselves this endpoint emitted ordinals instead, which threw in the portal's
/// Device Outcomes report and silently zeroed the Dashboard's completed-session count.
/// </summary>
public sealed class SessionHistoryRecordSerializationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static SessionHistoryRecord BuildRecord(SessionState finalState = SessionState.SessionCompleted) => new()
    {
        SessionId = Guid.NewGuid(),
        FinalState = finalState,
        DeviceSerialNumber = "SN-1",
        DeviceManufacturer = "Contoso",
        DeviceModel = "Model X",
        PreFlightAuthorizationResult = PreFlightAuthorizationResult.MatchedAutopilotV1,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
        TerminalAt = DateTimeOffset.UtcNow,
    };

    [Theory]
    [InlineData(SessionState.SessionCompleted, "SessionCompleted")]
    [InlineData(SessionState.SessionFailed, "SessionFailed")]
    [InlineData(SessionState.SessionExpired, "SessionExpired")]
    [InlineData(SessionState.SessionNotAuthorized, "SessionNotAuthorized")]
    public void FinalState_serializes_as_its_name_not_its_ordinal(SessionState state, string expected)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(BuildRecord(state), WebOptions));

        var finalState = doc.RootElement.GetProperty("finalState");
        finalState.ValueKind.Should().Be(JsonValueKind.String,
            "the portal compares finalState against SessionState names, so an ordinal breaks every consumer");
        finalState.GetString().Should().Be(expected);
    }

    [Fact]
    public void PreFlightAuthorizationResult_serializes_as_its_name_not_its_ordinal()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(BuildRecord(), WebOptions));

        var result = doc.RootElement.GetProperty("preFlightAuthorizationResult");
        result.ValueKind.Should().Be(JsonValueKind.String);
        result.GetString().Should().Be("MatchedAutopilotV1");
    }

    [Fact]
    public void FailedStepName_serializes_as_its_name_when_present()
    {
        var record = new SessionHistoryRecord
        {
            SessionId = Guid.NewGuid(),
            FinalState = SessionState.SessionFailed,
            DeviceSerialNumber = "SN-2",
            DeviceManufacturer = "Contoso",
            DeviceModel = "Model X",
            FailedStepName = ImagingStepName.ApplyImage,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            TerminalAt = DateTimeOffset.UtcNow,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(record, WebOptions));

        doc.RootElement.GetProperty("failedStepName").GetString().Should().Be("ApplyImage");
    }
}
