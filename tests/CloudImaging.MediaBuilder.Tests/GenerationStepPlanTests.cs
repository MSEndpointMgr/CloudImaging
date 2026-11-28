using CloudImaging.MediaBuilder.ViewModels;
using FluentAssertions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

public sealed class GenerationStepPlanTests
{
    [Fact]
    public void PlanSteps_LeavesOutEveryStepForAnOptionThatWasNotChosen()
    {
        var steps = GenerateBootImageViewModel.PlanSteps(downloadClient: false, hpTools: false, windowsRe: false, drivers: false, supportTools: false);

        steps.Select(s => s.Label).Should().Equal(
            "Verify ADK installation",
            "Copy WinPE base files",
            "Mount WIM for customization",
            "Inject Cloud Imaging Client",
            "Unmount and commit WIM",
            "Copy output and hash",
            "Complete");
    }

    [Fact]
    public void PlanSteps_ListsTheChosenOptions_InTheOrderTheyRun()
    {
        var steps = GenerateBootImageViewModel.PlanSteps(downloadClient: true, hpTools: true, windowsRe: true, drivers: true, supportTools: true);

        steps.Select(s => s.Label).Should().Equal(
            "Download Cloud Imaging Client",
            "Download HP firmware tools",
            "Verify ADK installation",
            "Copy WinPE base files",
            "Extract Windows RE",
            "Mount WIM for customization",
            "Inject Cloud Imaging Client",
            "Inject drivers",
            "Copy support tools",
            "Unmount and commit WIM",
            "Copy output and hash",
            "Complete");
        steps.Select(s => s.StartPercent).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        steps.Where(s => s.Label.StartsWith("Download", StringComparison.Ordinal)).Should().OnlyContain(s => s.StartPercent > 1 && s.StartPercent < 5, "the downloads run after the UAC prompt (1) and before the elevated worker (5)");
    }
}
