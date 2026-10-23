using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

public sealed class OverallProgressCalculatorTests
{
    private static ImagingStep Step(ImagingStepName name, ImagingStepStatus status, int? percent = null, int? warnings = null) =>
        new() { StepName = name, Status = status, StepProgressPercent = percent, WarningCount = warnings };

    private static List<ImagingStep> RequiredStepsCompleted() =>
    [
        Step(ImagingStepName.FormatDisk, ImagingStepStatus.Completed),
        Step(ImagingStepName.DownloadImage, ImagingStepStatus.Completed),
        Step(ImagingStepName.ApplyImage, ImagingStepStatus.Completed),
        Step(ImagingStepName.ConfigureBoot, ImagingStepStatus.Completed),
        Step(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.Completed),
    ];

    [Fact]
    public void Calculate_PrepareSetupInProgress_HoldsOverallAtEighty()
    {
        var steps = new List<ImagingStep>
        {
            Step(ImagingStepName.FormatDisk, ImagingStepStatus.Completed),
            Step(ImagingStepName.DownloadImage, ImagingStepStatus.Completed),
            Step(ImagingStepName.ApplyImage, ImagingStepStatus.Completed),
            Step(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress, percent: 60),
        };

        OverallProgressCalculator.Calculate(steps).Should().Be(80);
    }

    [Fact]
    public void Calculate_AllStepsIncludingPrepareSetupCompleted_ReturnsHundred()
    {
        var steps = RequiredStepsCompleted();
        steps.Add(Step(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed));

        OverallProgressCalculator.Calculate(steps).Should().Be(100);
    }

    [Fact]
    public void IsPipelineComplete_RequiredStepsCompletedWithoutPrepareSetup_ReturnsTrue()
    {
        OverallProgressCalculator.IsPipelineComplete(RequiredStepsCompleted()).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(2)]
    public void IsPipelineComplete_PrepareSetupCompleted_ReturnsTrue(int? warningCount)
    {
        var steps = RequiredStepsCompleted();
        steps.Add(Step(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed, warnings: warningCount));

        OverallProgressCalculator.IsPipelineComplete(steps).Should().BeTrue();
    }

    [Fact]
    public void IsPipelineComplete_PrepareSetupInProgress_ReturnsFalse()
    {
        var steps = RequiredStepsCompleted();
        steps.Add(Step(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress));

        OverallProgressCalculator.IsPipelineComplete(steps).Should().BeFalse();
    }

    [Fact]
    public void IsPipelineComplete_PrepareSetupFailed_ReturnsFalse()
    {
        var steps = RequiredStepsCompleted();
        steps.Add(Step(ImagingStepName.PrepareSetup, ImagingStepStatus.Failed));

        OverallProgressCalculator.IsPipelineComplete(steps).Should().BeFalse();
    }

    [Theory]
    [InlineData(ImagingStepName.FormatDisk)]
    [InlineData(ImagingStepName.DownloadImage)]
    [InlineData(ImagingStepName.ApplyImage)]
    [InlineData(ImagingStepName.ConfigureBoot)]
    [InlineData(ImagingStepName.ApplyRecoveryImage)]
    public void IsPipelineComplete_RequiredStepMissing_ReturnsFalse(ImagingStepName missing)
    {
        var steps = RequiredStepsCompleted().Where(s => s.StepName != missing).ToList();
        steps.Add(Step(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed));

        OverallProgressCalculator.IsPipelineComplete(steps).Should().BeFalse();
    }

    [Fact]
    public void IsPipelineComplete_NoSteps_ReturnsFalse()
    {
        OverallProgressCalculator.IsPipelineComplete([]).Should().BeFalse();
        OverallProgressCalculator.IsPipelineComplete(null).Should().BeFalse();
    }

    [Fact]
    public void CurrentOrLastStepName_PrepareSetupFurthestCompleted_ReturnsPrepareSetup()
    {
        var steps = new List<ImagingStep>
        {
            Step(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed),
            Step(ImagingStepName.ApplyImage, ImagingStepStatus.Completed),
            Step(ImagingStepName.FormatDisk, ImagingStepStatus.Completed),
            Step(ImagingStepName.DownloadImage, ImagingStepStatus.Completed),
        };

        OverallProgressCalculator.CurrentOrLastStepName(steps).Should().Be("PrepareSetup");
    }

    [Fact]
    public void CurrentOrLastStepName_ConfigureBootCompletedAfterPrepareSetup_ReturnsConfigureBoot()
    {
        var steps = new List<ImagingStep>
        {
            Step(ImagingStepName.ConfigureBoot, ImagingStepStatus.Completed),
            Step(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed),
            Step(ImagingStepName.ApplyImage, ImagingStepStatus.Completed),
        };

        OverallProgressCalculator.CurrentOrLastStepName(steps).Should().Be("ConfigureBoot");
    }

    [Fact]
    public void UnknownStepName_DoesNotThrow()
    {
        var steps = RequiredStepsCompleted();
        steps.Add(Step((ImagingStepName)99, ImagingStepStatus.Completed));

        var act = () =>
        {
            OverallProgressCalculator.Calculate(steps);
            OverallProgressCalculator.IsPipelineComplete(steps);
            OverallProgressCalculator.CurrentOrLastStepName(steps);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void PipelineOrder_ListsEveryStepName_InDeviceOrder()
    {
        OverallProgressCalculator.PipelineOrder.Should().Equal(
            ImagingStepName.FormatDisk,
            ImagingStepName.DownloadImage,
            ImagingStepName.ApplyImage,
            ImagingStepName.PrepareSetup,
            ImagingStepName.ConfigureBoot,
            ImagingStepName.ApplyRecoveryImage);
        OverallProgressCalculator.PipelineOrder.Should().BeEquivalentTo(Enum.GetValues<ImagingStepName>(),
            "every step the Client can report must have a place in the pipeline order");
    }
}
