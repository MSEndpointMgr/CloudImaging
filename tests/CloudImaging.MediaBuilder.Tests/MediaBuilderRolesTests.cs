using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Media Builder role hierarchy: Administrator implies Operator implies Technician, and the
/// expanded set drives the Media Builder capabilities (data-model section 5).
/// </summary>
public sealed class MediaBuilderRolesTests
{
    private const string Administrator = "CloudImaging.Administrator";
    private const string Operator = "CloudImaging.Operator";
    private const string Technician = "CloudImaging.Technician";

    [Fact]
    public void RoleConstants_MatchTheAppRoleValues_OnTheMediaBuilderRegistration()
    {
        MediaBuilderRoles.Administrator.Should().Be(Administrator);
        MediaBuilderRoles.Operator.Should().Be(Operator);
        MediaBuilderRoles.Technician.Should().Be(Technician);
    }

    [Fact]
    public void Expand_Administrator_ReturnsAdministratorOperatorAndTechnician()
    {
        MediaBuilderRoles.Expand([Administrator]).Should().BeEquivalentTo([Administrator, Operator, Technician]);
    }

    [Fact]
    public void Expand_Operator_ReturnsOperatorAndTechnician()
    {
        MediaBuilderRoles.Expand([Operator]).Should().BeEquivalentTo([Operator, Technician]);
    }

    [Fact]
    public void Expand_Technician_ReturnsTechnicianOnly()
    {
        MediaBuilderRoles.Expand([Technician]).Should().BeEquivalentTo([Technician]);
    }

    [Fact]
    public void Expand_NoRoles_ReturnsEmptySet()
    {
        MediaBuilderRoles.Expand([]).Should().BeEmpty();
    }

    [Fact]
    public void Expand_DifferentlyCasedRole_IsNotMatched()
    {
        MediaBuilderRoles.Expand(["cloudimaging.administrator", "CLOUDIMAGING.OPERATOR"])
            .Should().NotContain([Administrator, Operator, Technician], "role values are compared ordinally, as in the token");
    }

    [Fact]
    public void Expand_UnknownRoleValues_AreIgnored()
    {
        var expanded = MediaBuilderRoles.Expand(["CloudImaging.Reader", "CloudImaging.MediaBuilderAccess", Technician]);

        expanded.Should().Contain(Technician);
        expanded.Should().NotContain([Administrator, Operator], "no unknown value may imply a Media Builder role");
    }

    [Fact]
    public void Expand_DuplicateRoleValues_ReturnEachRoleOnce()
    {
        MediaBuilderRoles.Expand([Operator, Operator, Technician]).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(Administrator)]
    [InlineData(Operator)]
    [InlineData(Technician)]
    public void Expand_AppliedTwice_ReturnsTheSameSet(string role)
    {
        var once = MediaBuilderRoles.Expand([role]);

        MediaBuilderRoles.Expand(once).Should().BeEquivalentTo(once);
    }

    [Theory]
    [InlineData(Administrator)]
    [InlineData(Operator)]
    [InlineData(Technician)]
    public void Expand_AnyRole_IsSupersetOfTheInput(string role)
    {
        MediaBuilderRoles.Expand([role]).Should().Contain(role);
    }

    [Fact]
    public void Implications_EveryEntry_AlreadyContainsTheImplicationsOfItsImpliedRoles()
    {
        foreach (var (role, implied) in MediaBuilderRoles.Implications)
        {
            foreach (var impliedRole in implied)
            {
                MediaBuilderRoles.Implications[impliedRole].Except(implied).Should().BeEmpty($"the closure of {role} must be listed explicitly");
            }
        }
    }

    [Fact]
    public void Implications_NoRole_ImpliesAdministrator()
    {
        MediaBuilderRoles.Implications.Values.Should().NotContain(implied => implied.Contains(Administrator));
    }

    [Theory]
    [InlineData(Administrator, true, true, true)]
    [InlineData(Operator, true, true, true)]
    [InlineData(Technician, true, false, false)]
    [InlineData(null, false, false, false)]
    public void ExpandedSet_SingleAssignedRole_DrivesTheCapabilityTable(string? role, bool hasMediaBuilderRole, bool canGenerateBootImage, bool canSeePreProduction)
    {
        var expanded = MediaBuilderRoles.Expand(role is null ? [] : [role]);

        expanded.Contains(MediaBuilderRoles.Technician).Should().Be(hasMediaBuilderRole);
        expanded.Contains(MediaBuilderRoles.Operator).Should().Be(canGenerateBootImage);
        expanded.Contains(MediaBuilderRoles.Operator).Should().Be(canSeePreProduction);
    }

    [Fact]
    public void EntraAuthenticationService_BeforeSignIn_GrantsNoCapability()
    {
        var svc = new EntraAuthenticationService("test-client-id", "test-tenant-id", "api://test-client-id/.default", NullLogger<EntraAuthenticationService>.Instance);

        svc.HasMediaBuilderRole.Should().BeFalse();
        svc.CanGenerateBootImage.Should().BeFalse("generation must never be assumed before a sign-in completes");
        svc.CanSeePreProductionBootImages.Should().BeFalse();
    }
}
