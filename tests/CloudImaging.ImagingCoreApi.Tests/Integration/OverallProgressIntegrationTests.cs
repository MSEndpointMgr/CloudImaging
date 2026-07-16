using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Integration tests for overall imaging completion percentage calculation (T047d, FR-007).
/// Validates the OverallProgressCalculator logic used by ReportProgressFunction.
/// </summary>
public sealed class OverallProgressIntegrationTests
{
    // ── 0 steps ───────────────────────────────────────────────────────────────

    [Fact]
    public void ZeroSteps_Returns_ZeroPercent()
    {
        OverallProgressCalculator.Calculate([]).Should().Be(0);
        OverallProgressCalculator.Calculate(null).Should().Be(0);
    }

    // ── Single step complete ──────────────────────────────────────────────────

    [Fact]
    public void OneOfThreeStepsCompleted_Returns_33Percent()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk, Status = ImagingStepStatus.Completed },
        };

        var result = OverallProgressCalculator.Calculate(steps);
        result.Should().BeInRange(33, 34,
            "1 of 3 equal-weight steps completing contributes ~33%");
    }

    [Fact]
    public void TwoOfThreeStepsCompleted_Returns_67Percent()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.Completed },
        };

        var result = OverallProgressCalculator.Calculate(steps);
        result.Should().BeInRange(66, 67,
            "2 of 3 equal-weight steps completing contributes ~67%");
    }

    [Fact]
    public void AllThreeStepsCompleted_Returns_100Percent()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.ApplyImage,    Status = ImagingStepStatus.Completed },
        };

        OverallProgressCalculator.Calculate(steps).Should().Be(100);
    }

    // ── In-progress sub-progress ──────────────────────────────────────────────

    [Fact]
    public void InProgressStep_WithSubProgress_ContributesFractionalShare()
    {
        // FormatDisk complete (33%) + DownloadImage 50% in-progress (33%*0.5 = 16.5%) ≈ 50%
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.InProgress, StepProgressPercent = 50 },
        };

        var result = OverallProgressCalculator.Calculate(steps);
        result.Should().BeInRange(49, 51,
            "FormatDisk done (33%) + Download 50% in-progress (~17%) = ~50%");
    }

    [Fact]
    public void InProgressStep_WithZeroSubProgress_DoesNotContribute()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk, Status = ImagingStepStatus.InProgress, StepProgressPercent = 0 },
        };

        OverallProgressCalculator.Calculate(steps).Should().Be(0,
            "an InProgress step with 0% sub-progress contributes nothing to overall");
    }

    // ── Failed step ───────────────────────────────────────────────────────────

    [Fact]
    public void FailedStep_DoesNotContributeToOverallPercent()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.Failed },
        };

        var result = OverallProgressCalculator.Calculate(steps);
        result.Should().BeInRange(33, 34,
            "a Failed step contributes 0% — only the completed step counts");
    }

    // ── ActiveStepName ────────────────────────────────────────────────────────

    [Fact]
    public void ActiveStepName_ReturnsInProgressStepName()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.InProgress },
        };

        OverallProgressCalculator.ActiveStepName(steps).Should().Be("DownloadImage");
    }

    [Fact]
    public void ActiveStepName_ReturnsNull_WhenNoStepIsInProgress()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk, Status = ImagingStepStatus.Completed },
        };

        OverallProgressCalculator.ActiveStepName(steps).Should().BeNull();
    }

    [Fact]
    public void Calculate_NeverExceeds100Percent()
    {
        // Guard against rounding overflow
        var steps = Enumerable.Range(0, 10)
            .Select(_ => new ImagingStep
            {
                StepName = ImagingStepName.ApplyImage,
                Status   = ImagingStepStatus.Completed,
            })
            .ToList();

        OverallProgressCalculator.Calculate(steps).Should().BeLessOrEqualTo(100);
    }
}
