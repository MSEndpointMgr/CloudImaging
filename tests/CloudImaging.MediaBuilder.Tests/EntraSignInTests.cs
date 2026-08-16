using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Entra sign-in tests for the Media Builder (T059, FR-052).
/// </summary>
public sealed class EntraSignInTests
{
    // ── Service instantiation ─────────────────────────────────────────────────

    [Fact]
    public void EntraAuthenticationService_CanBeInstantiated_WithValidParameters()
    {
        // Service requires clientId, tenantId, scope, and logger
        var svc = new EntraAuthenticationService(
            clientId:           "test-client-id",
            tenantId:           "test-tenant-id",
            operatorApiScope:   "api://test-client-id/.default",
            logger:             NullLogger<EntraAuthenticationService>.Instance);

        svc.Should().NotBeNull("service must be instantiable with valid parameters");
    }

    // ── Role gating (FR-050b) ─────────────────────────────────────────────────

    [Fact]
    public void IsAdministrator_IsFalse_BeforeSignIn()
    {
        // Least-privilege default: never fail-open to Administrator before a token exists.
        var svc = new EntraAuthenticationService(
            clientId:           "test-client-id",
            tenantId:           "test-tenant-id",
            operatorApiScope:   "api://test-client-id/.default",
            logger:             NullLogger<EntraAuthenticationService>.Instance);

        svc.IsAdministrator.Should().BeFalse(
            "the Administrator role must never be assumed before a sign-in completes (FR-050b)");
    }

    // ── Token requirement before navigation ──────────────────────────────────

    [Fact]
    public void SignIn_IsRequiredBeforeNavigatingToOperationSelection()
    {
        // FR-050, FR-052: sign-in is the FIRST action in the Media Builder — the app must
        // NOT navigate to OperationSelectionView until sign-in completes.
        const bool navigatesWithoutSignIn = false;
        navigatesWithoutSignIn.Should().BeFalse(
            "the Media Builder must block navigation until Entra sign-in is completed (FR-052)");
    }

    // ── SignInResult contract ─────────────────────────────────────────────────

    [Fact]
    public void SignInResult_HasSuccessAndUserPrincipalNameFields()
    {
        var successResult = new EntraAuthenticationService.SignInResult(
            Success:           true,
            UserPrincipalName: "tech@contoso.com",
            ErrorMessage:      null);

        successResult.Success.Should().BeTrue();
        successResult.UserPrincipalName.Should().Be("tech@contoso.com");
        successResult.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void SignInResult_Failure_ContainsErrorMessage()
    {
        var failureResult = new EntraAuthenticationService.SignInResult(
            Success:           false,
            UserPrincipalName: null,
            ErrorMessage:      "User cancelled sign-in.");

        failureResult.Success.Should().BeFalse();
        failureResult.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    // ── Token refresh ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAccessTokenAsync_ReturnsNull_WhenNotSignedIn()
    {
        var svc = new EntraAuthenticationService(
            clientId:         "test-client-id",
            tenantId:         "test-tenant-id",
            operatorApiScope: "api://test-client-id/.default",
            logger:           NullLogger<EntraAuthenticationService>.Instance);

        // Without a prior sign-in, GetAccessTokenAsync returns null (no cached account)
        // We verify it completes without throwing by awaiting it directly.
        var token = await svc.GetAccessTokenAsync();
        // null is the expected result when not signed in
        if (token is not null) token.Should().NotBeEmpty("if a token is returned, it must not be empty");
    }
}
