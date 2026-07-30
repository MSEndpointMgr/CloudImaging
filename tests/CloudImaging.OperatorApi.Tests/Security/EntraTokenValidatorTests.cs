using CloudImaging.OperatorApi.Security;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace CloudImaging.OperatorApi.Tests.Security;

/// <summary>
/// Unit tests for <see cref="EntraTokenValidator"/> covering configuration guards and the
/// malformed-token rejection path (no network / JWKS fetch required). Full signature/issuer/
/// audience validation is exercised end to end against the live tenant JWKS in deployed
/// environments.
/// </summary>
public sealed class EntraTokenValidatorTests
{
    private static EntraValidationOptions ValidOptions() => new()
    {
        TenantId = "11111111-1111-1111-1111-111111111111",
        ValidAudiences = ["22222222-2222-2222-2222-222222222222"],
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_Throws_WhenTenantIdMissing(string? tenantId)
    {
        var act = () => new EntraTokenValidator(new EntraValidationOptions
        {
            TenantId = tenantId!,
            ValidAudiences = ["22222222-2222-2222-2222-222222222222"],
        });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_Throws_WhenNoValidAudiences()
    {
        var act = () => new EntraTokenValidator(new EntraValidationOptions
        {
            TenantId = "11111111-1111-1111-1111-111111111111",
            ValidAudiences = [],
        });

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    [InlineData("")]
    public async Task ValidateAsync_Throws_ForMalformedToken(string token)
    {
        var validator = new EntraTokenValidator(ValidOptions());

        var act = async () => await validator.ValidateAsync(token, CancellationToken.None);

        await act.Should().ThrowAsync<SecurityTokenMalformedException>();
    }
}
