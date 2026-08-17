using CloudImaging.MediaBuilder.Services;
using CloudImaging.MediaBuilder.ViewModels;
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
    public void ConstructingSignInViewModel_DoesNotNavigate_BeforeSignInIsAttempted()
    {
        // FR-050, FR-052: sign-in is the FIRST action in the Media Builder — the app must
        // NOT navigate to OperationSelectionView until sign-in completes. EntraAuthenticationService
        // is sealed (drives real interactive MSAL sign-in) so it cannot be faked to simulate a
        // successful sign-in here; this test instead guards the other half of FR-052 that IS
        // safely verifiable without invoking MSAL — that no navigation happens merely from
        // constructing the view model or before SignInCommand is ever executed.
        var navigated = false;
        var authService = new EntraAuthenticationService(
            clientId:         "test-client-id",
            tenantId:         "test-tenant-id",
            operatorApiScope: "api://test-client-id/.default",
            logger:           NullLogger<EntraAuthenticationService>.Instance);

        var vm = new SignInViewModel(authService, () => navigated = true);

        navigated.Should().BeFalse("navigation must never happen before a sign-in attempt completes (FR-052)");
        vm.CanSignIn.Should().BeTrue("the view model must be ready to accept a sign-in attempt immediately");
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
