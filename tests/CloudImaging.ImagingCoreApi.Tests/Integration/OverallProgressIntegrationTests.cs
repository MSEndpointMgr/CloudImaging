using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Integration;

/// <summary>
/// Integration tests for overall imaging completion percentage calculation (T047d, FR-007).
/// Validates the OverallProgressCalculator logic used by ReportProgressFunction.
///
/// The pipeline has 5 steps, weighted to match the bands the Client's own progress ring uses
/// (FormatDisk 8, DownloadImage 47, ApplyImage 25, ConfigureBoot 5, ApplyRecoveryImage 15).
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

    [Fact]
    public void PipelineStepCount_Matches_EveryDeclaredStep()
    {
        OverallProgressCalculator.PipelineStepCount.Should().Be(
            Enum.GetValues<ImagingStepName>().Length,
            "every step the Client can report must be weighted, otherwise a completed session " +
            "never reaches 100% and never transitions to SessionCompleted");
    }

    // ── Single step complete ──────────────────────────────────────────────────

    [Fact]
    public void FirstStepCompleted_Returns_ItsShare()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk, Status = ImagingStepStatus.Completed },
        };

        OverallProgressCalculator.Calculate(steps).Should().Be(8);
    }

    [Fact]
    public void TwoStepsCompleted_Returns_SumOfTheirShares()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.Completed },
        };

        OverallProgressCalculator.Calculate(steps).Should().Be(55);
    }

    [Fact]
    public void AllFiveStepsCompleted_Returns_100Percent()
    {
        var steps = Enum.GetValues<ImagingStepName>()
            .Select(name => new ImagingStep { StepName = name, Status = ImagingStepStatus.Completed })
            .ToList();

        OverallProgressCalculator.Calculate(steps).Should().Be(100);
    }

    [Fact]
    public void OsStepsCompleted_ButRecoveryOutstanding_IsNot100Percent()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.ApplyImage,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.ConfigureBoot, Status = ImagingStepStatus.Completed },
        };

        OverallProgressCalculator.Calculate(steps).Should().Be(85,
            "the recovery image step still has to run before the session is done");
    }

    // ── In-progress sub-progress ──────────────────────────────────────────────

    [Fact]
    public void InProgressStep_WithSubProgress_ContributesFractionalShare()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.InProgress, StepProgressPercent = 50 },
        };

        OverallProgressCalculator.Calculate(steps).Should().Be(32,
            "FormatDisk done (8%) + Download 50% of its 47% share (23.5%) = ~32%");
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

        OverallProgressCalculator.Calculate(steps).Should().Be(8,
            "a Failed step contributes 0% — only the completed step counts");
    }

    // ── CurrentOrLastStepName ─────────────────────────────────────────────────

    [Fact]
    public void CurrentOrLastStepName_ReturnsInProgressStepName()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.InProgress },
        };

        OverallProgressCalculator.CurrentOrLastStepName(steps).Should().Be("DownloadImage");
    }

    [Fact]
    public void CurrentOrLastStepName_ReturnsFailedStep_SoTheFailedTabShowsWhereItStopped()
    {
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.FormatDisk,         Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.DownloadImage,      Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.ApplyImage,         Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.ConfigureBoot,      Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.ApplyRecoveryImage, Status = ImagingStepStatus.Failed },
        };

        OverallProgressCalculator.CurrentOrLastStepName(steps).Should().Be("ApplyRecoveryImage");
    }

    [Fact]
    public void CurrentOrLastStepName_ReturnsFurthestCompletedStep_WhenNothingIsRunning()
    {
        // Table Storage returns step rows unordered, so the furthest step must win by pipeline
        // position rather than by list order.
        var steps = new List<ImagingStep>
        {
            new() { StepName = ImagingStepName.DownloadImage, Status = ImagingStepStatus.Completed },
            new() { StepName = ImagingStepName.FormatDisk,    Status = ImagingStepStatus.Completed },
        };

        OverallProgressCalculator.CurrentOrLastStepName(steps).Should().Be("DownloadImage");
    }

    [Fact]
    public void CurrentOrLastStepName_ReturnsNull_WhenNoStepsReported()
    {
        OverallProgressCalculator.CurrentOrLastStepName([]).Should().BeNull();
        OverallProgressCalculator.CurrentOrLastStepName(null).Should().BeNull();
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
