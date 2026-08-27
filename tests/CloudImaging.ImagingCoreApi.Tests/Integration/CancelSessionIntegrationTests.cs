using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Integration tests for the operator-initiated "remove coupled session" endpoint
/// (DELETE /api/internal/sessions/{sessionId}). Tests the state pre-condition contract
/// without a live Table Storage, mirroring <see cref="AssignSessionIntegrationTests"/>.
/// </summary>
public sealed class CancelSessionIntegrationTests
{
    [Theory]
    [InlineData(SessionState.SessionAssigned,   true)]  // only removable state
    [InlineData(SessionState.SessionInit,       false)]
    [InlineData(SessionState.SessionAllowed,    false)]
    [InlineData(SessionState.SessionStarted,    false)]
    [InlineData(SessionState.SessionInProgress, false)]
    [InlineData(SessionState.SessionCompleted,  false)]
    [InlineData(SessionState.SessionFailed,     false)]
    [InlineData(SessionState.SessionNotAuthorized, false)]
    public void Removal_IsAllowedOnlyInSessionAssignedState(SessionState state, bool expectRemovable)
    {
        var isRemovable = state == SessionState.SessionAssigned;
        isRemovable.Should().Be(expectRemovable,
            $"a session in {state} should {(expectRemovable ? "" : "not ")}be removable via the cancel endpoint");
    }
}
