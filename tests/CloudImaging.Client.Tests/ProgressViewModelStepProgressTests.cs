using System.Globalization;
using CloudImaging.Client.ViewModels;
using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Covers the step-scoped progress bar at the bottom of ProgressView: it tracks the operation
/// currently running (download / apply / recovery, and any step added later) rather than the
/// whole session, so it must empty itself on every step transition instead of carrying the
/// previous step's fill into a step that reports nothing.
/// </summary>
public sealed class ProgressViewModelStepProgressTests
{
    [Fact]
    public void StepPercent_IsClampedAndOnlyNotifiesOnRealChange()
    {
        var vm = new ProgressViewModel();
        var raised = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProgressViewModel.StepPercent)) { raised++; } };

        vm.StepPercent = 42;
        vm.StepPercent = 42; // the download loop re-reports the same integer percent constantly
        vm.StepPercent = 250;
        vm.StepPercent = -5;

        vm.StepPercent.Should().Be(0);
        raised.Should().Be(3, "repeated identical values must not queue redundant UI updates");
    }

    [Fact]
    public void UpdateStep_ResetsStepProgressAndTransferDetail()
    {
        var vm = new ProgressViewModel();
        vm.StepPercent = 100;
        vm.SetTransferProgress(5_000_000_000, 5_000_000_000);

        vm.UpdateStep(ImagingStepName.ConfigureBoot, ImagingStepStatus.InProgress);

        vm.StepPercent.Should().Be(0);
        vm.TransferDetail.Should().BeNull();
        vm.HasTransferDetail.Should().BeFalse();
    }

    [Fact]
    public void SetTransferProgress_UsesGigabytesForBothFiguresWhenTotalIsLarge()
    {
        var vm = new ProgressViewModel();

        // 512 MB of a 4 GB image: the transferred side stays in GB so the two numbers remain
        // directly comparable instead of reading "512.00 MB of 4.00 GB".
        vm.SetTransferProgress(512L * 1024 * 1024, 4L * 1024 * 1024 * 1024);

        vm.TransferDetail.Should().Be($"{Number(0.5)} GB of {Number(4)} GB");
        vm.HasTransferDetail.Should().BeTrue();
    }

    [Fact]
    public void SetTransferProgress_UsesMegabytesForSmallTotals()
    {
        var vm = new ProgressViewModel();

        vm.SetTransferProgress(64L * 1024 * 1024, 256L * 1024 * 1024);

        vm.TransferDetail.Should().Be($"{Number(64)} MB of {Number(256)} MB");
    }

    [Fact]
    public void SetTransferProgress_OmitsTotalWhenContentLengthIsUnknown()
    {
        var vm = new ProgressViewModel();

        vm.SetTransferProgress(128L * 1024 * 1024, -1);

        vm.TransferDetail.Should().Be($"{Number(128)} MB downloaded");
    }

    [Fact]
    public void StepPercentText_ShowsTheStepPercent_NextToTheByteCounter()
    {
        // 23% into the download is 19% overall (8% for the disk plus 23% of the download's 47%); the step figure is the portal's.
        var vm = new ProgressViewModel();
        vm.StepPercentText.Should().BeNull();

        vm.StepPercent = 23;
        vm.SetTransferProgress(1L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024);

        vm.StepPercentText.Should().Be("23%");
        vm.HasStepPercentText.Should().BeTrue();
        vm.TransferDetail.Should().Be($"{Number(1)} GB of {Number(4)} GB");

        vm.UpdateStep(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress);
        vm.StepPercentText.Should().BeNull("a new step starts without a figure until it reports one");
        vm.StepPercent = 40;
        vm.StepPercentText.Should().Be("40%");
        vm.TransferDetail.Should().BeNull();
    }

    /// <summary>Formats a figure the way the view model does, so these assertions hold on build
    /// agents and workstations whose decimal separator is not a period.</summary>
    private static string Number(double value) => value.ToString("0.00", CultureInfo.CurrentCulture);

    [Fact]
    public void PrepareSetup_AppearsBetweenApplyImageAndConfigureBoot_OnlyWhenExpected()
    {
        var vm = new ProgressViewModel();
        vm.Steps.Select(s => s.Label).Should().NotContain("Prepare Setup");

        vm.ConfigureSteps(includePrepareSetup: true);
        vm.Steps.Select(s => s.Label).Should().Equal("Format Disk", "Download Image", "Apply Image", "Prepare Setup", "Configure Boot", "Apply Recovery Image");
        vm.StepSubtitle.Should().StartWith("Step 1 of 6");

        vm.ConfigureSteps(includePrepareSetup: false);
        vm.Steps.Should().HaveCount(5);
    }

    [Fact]
    public void CompletedWithWarnings_ShowsTheWarningState()
    {
        var vm = new ProgressViewModel();
        vm.ConfigureSteps(includePrepareSetup: true);

        vm.UpdateStep(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress);
        vm.UpdateStep(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed, warningCount: 1);

        var step = vm.Steps.Single(s => s.Step == ImagingStepName.PrepareSetup);
        step.State.Should().Be(ProgressStepState.Warning);
        step.Caption.Should().Be("Completed with warnings");
        vm.CurrentStepTitle.Should().Be("Prepare Setup");
    }

    [Fact]
    public void UpdatingAStepThatIsNotShown_IsIgnored()
    {
        var vm = new ProgressViewModel();

        var act = () => vm.UpdateStep(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress);

        act.Should().NotThrow();
    }
}
