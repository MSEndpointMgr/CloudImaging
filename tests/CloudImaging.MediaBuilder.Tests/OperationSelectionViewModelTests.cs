using CloudImaging.MediaBuilder.ViewModels;
using FluentAssertions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// OperationSelectionView prerequisite gating tests: Windows ADK (FR-050a) and the
/// Administrator role (FR-050b).
///
/// The Generate Boot Image workflow requires the Windows ADK and the Administrator role;
/// when either is missing the user must be informed on this screen and blocked from
/// navigating. Prepare USB has no ADK or role dependency and stays available.
/// </summary>
public sealed class OperationSelectionViewModelTests
{
    [Fact]
    public void GenerateBootImage_IsBlocked_AndWarned_WhenAdkMissing()
    {
        var vm = new OperationSelectionViewModel(_ => { }, isAdkInstalled: () => false)
        {
            SelectedOperation = "GenerateBootImage",
        };

        vm.ShowAdkWarning.Should().BeTrue("the ADK prerequisite must be surfaced on the prior screen");
        vm.CanContinue.Should().BeFalse("navigation to Generate Boot Image is blocked without the ADK");
    }

    [Fact]
    public void GenerateBootImage_IsAllowed_WhenAdkPresent()
    {
        var vm = new OperationSelectionViewModel(_ => { }, isAdkInstalled: () => true)
        {
            SelectedOperation = "GenerateBootImage",
        };

        vm.ShowAdkWarning.Should().BeFalse();
        vm.CanContinue.Should().BeTrue();
    }

    [Fact]
    public void PrepareUsb_IsAlwaysAvailable_EvenWhenAdkMissing()
    {
        var vm = new OperationSelectionViewModel(_ => { }, isAdkInstalled: () => false)
        {
            SelectedOperation = "PrepareUSB",
        };

        vm.ShowAdkWarning.Should().BeFalse("Prepare USB does not depend on the ADK");
        vm.CanContinue.Should().BeTrue();
    }

    [Fact]
    public void SwitchingAwayFromGenerate_ClearsWarning_WhenAdkMissing()
    {
        var vm = new OperationSelectionViewModel(_ => { }, isAdkInstalled: () => false)
        {
            SelectedOperation = "GenerateBootImage",
        };
        vm.ShowAdkWarning.Should().BeTrue();

        vm.SelectedOperation = "PrepareUSB";
        vm.ShowAdkWarning.Should().BeFalse("the warning only applies to the ADK-dependent operation");
    }

    // ── Role-based gating (FR-050b) ────────────────────────────────────────────

    [Fact]
    public void GenerateBootImage_IsBlocked_AndRoleRestricted_WhenNotAdministrator()
    {
        var vm = new OperationSelectionViewModel(_ => { }, isAdkInstalled: () => true, isAdministrator: false)
        {
            SelectedOperation = "GenerateBootImage",
        };

        vm.IsRestrictedByRole.Should().BeTrue("a Technician (or no-role) user must not see Generate Boot Image as available");
        vm.IsBlockedByMissingAdk.Should().BeFalse("the ADK reason must not also fire when the role is the actual blocker");
        vm.IsGenerateBootImageAvailable.Should().BeFalse();
        vm.CanContinue.Should().BeFalse("navigation to Generate Boot Image is blocked without the Administrator role");
    }

    [Fact]
    public void GenerateBootImage_IsAllowed_WhenAdkPresent_AndAdministrator()
    {
        var vm = new OperationSelectionViewModel(_ => { }, isAdkInstalled: () => true, isAdministrator: true)
        {
            SelectedOperation = "GenerateBootImage",
        };

        vm.IsRestrictedByRole.Should().BeFalse();
        vm.IsGenerateBootImageAvailable.Should().BeTrue();
        vm.CanContinue.Should().BeTrue();
    }

    [Fact]
    public void PrepareUsb_IsAlwaysAvailable_EvenWhenNotAdministrator()
    {
        var vm = new OperationSelectionViewModel(_ => { }, isAdkInstalled: () => true, isAdministrator: false)
        {
            SelectedOperation = "PrepareUSB",
        };

        vm.CanContinue.Should().BeTrue("Prepare USB Device does not depend on the Administrator role");
    }
}
