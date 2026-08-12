using CloudImaging.MediaBuilder.ViewModels;
using FluentAssertions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// OperationSelectionView ADK prerequisite gating tests (FR-050a).
///
/// The Generate Boot Image workflow requires the Windows ADK; when it is missing the user
/// must be informed on this screen and blocked from navigating. Prepare USB has no ADK
/// dependency and stays available.
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
}
