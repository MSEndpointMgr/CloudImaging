using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Tests for the imaging workflow view model: background download/apply with no UI blocking (T047, FR-006, FR-007).
/// </summary>
public sealed class ImagingWorkflowViewModelTests
{
    // ── ProgressViewModel state machine ──────────────────────────────────────

    [Fact]
    public void ProgressViewModel_DefaultsToZeroPercent()
    {
        var vm = new ProgressViewModel();
        vm.OverallPercent.Should().Be(0, "initial progress must be 0%");
    }

    [Fact]
    public void ProgressViewModel_UpdateStep_ChangesState()
    {
        var vm = new ProgressViewModel();
        var formatStep = vm.Steps.Single(s => s.Step == ImagingStepName.FormatDisk);

        vm.UpdateStep(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress);
        formatStep.State.Should().Be(ProgressStepState.Active, "active step must report the Active state");

        vm.UpdateStep(ImagingStepName.FormatDisk, ImagingStepStatus.Completed);
        formatStep.State.Should().Be(ProgressStepState.Done, "Completed step must report the Done state");
        formatStep.Caption.Should().Contain("Completed", "a completed step shows a duration caption");
    }

    [Fact]
    public void ProgressViewModel_AllThreeSteps_CanBeTrackedIndependently()
    {
        var vm = new ProgressViewModel();

        vm.UpdateStep(ImagingStepName.FormatDisk,    ImagingStepStatus.Completed);
        vm.UpdateStep(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress);
        vm.UpdateStep(ImagingStepName.ApplyImage,    ImagingStepStatus.Pending);

        vm.Steps.Single(s => s.Step == ImagingStepName.FormatDisk).State.Should().Be(ProgressStepState.Done, "FormatDisk is Completed");
        vm.Steps.Single(s => s.Step == ImagingStepName.DownloadImage).State.Should().Be(ProgressStepState.Active, "DownloadImage is InProgress");
        vm.Steps.Single(s => s.Step == ImagingStepName.ApplyImage).State.Should().Be(ProgressStepState.Pending, "ApplyImage is Pending");
    }

    // ── Background processing must not block UI thread ────────────────────────

    [Fact]
    public void ImageDownloadService_ExposesAsync_DownloadMethod()
    {
        // The download method must be async to avoid blocking the WinPE UI thread (FR-007)
        var method = typeof(ImageDownloadService).GetMethod("EnsureLocalWimAsync");
        method.Should().NotBeNull("EnsureLocalWimAsync must exist");
        method!.ReturnType.Should().BeAssignableTo<System.Threading.Tasks.Task>(
            "download must return Task (async) to prevent UI blocking");
    }

    [Fact]
    public void ImageApplyService_ExposesAsync_ApplyMethod()
    {
        var method = typeof(ImageApplyService).GetMethod("ApplyAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().BeAssignableTo<System.Threading.Tasks.Task>(
            "DISM apply must be async to prevent UI thread blocking");
    }

    // ── SAS refresh coordinator ───────────────────────────────────────────────

    [Fact]
    public void SasRefreshCoordinator_DefaultThreshold_Is15Minutes()
    {
        // SAS is refreshed when < 15 minutes remain (FR-025)
        var threshold = SasRefreshCoordinator.RefreshThreshold;
        threshold.TotalMinutes.Should().Be(15, "SAS token refresh threshold is 15 minutes (FR-025)");
    }

    // ── Overall progress increases as steps complete ─────────────────────────

    [Fact]
    public void ProgressViewModel_OverallPercent_CanBeSet()
    {
        var vm = new ProgressViewModel();
        vm.OverallPercent = 50;
        vm.OverallPercent.Should().Be(50, "OverallPercent must be settable");
    }

    [Fact]
    public void ProgressViewModel_StatusMessage_CanBeSet()
    {
        var vm = new ProgressViewModel();
        vm.StatusMessage = "Applying image…";
        vm.StatusMessage.Should().Be("Applying image…");
    }

    // ── On-screen error detail truncation (FR-002b: no scrolling in ResultsView) ─────────────

    [Fact]
    public void BuildOnScreenErrorDetail_ShortDetail_IsReturnedUnchanged()
    {
        var detail = "diskpart exited with code -2147024894.";
        var result = InvokeBuildOnScreenErrorDetail(detail);
        result.Should().Be(detail);
    }

    [Fact]
    public void BuildOnScreenErrorDetail_LongDetail_TruncatesAtWordBoundary_NotMidWord()
    {
        // Regression test: a raw character-count cut previously produced "...DiskPar..." (cutting
        // the word "DiskPart" in half), which read as broken/unbounded rather than intentionally
        // summarized.
        var detail = string.Concat(Enumerable.Repeat("DiskPart succeeded in cleaning the disk. ", 20));

        var result = InvokeBuildOnScreenErrorDetail(detail);

        result.Should().Contain("See \"View Log\" for the full detail.");
        var summary = result[..result.IndexOf('…')];
        detail.Should().StartWith(summary, "the summary must be a verbatim, unmutated prefix of the original text");
        char.IsWhiteSpace(detail[summary.Length]).Should().BeTrue(
            "truncation must land right before a whitespace boundary, never mid-word");
    }

    private static string InvokeBuildOnScreenErrorDetail(string errorDetail)
    {
        var method = typeof(ImagingWorkflowViewModel).GetMethod(
            "BuildOnScreenErrorDetail",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (string)method.Invoke(null, [errorDetail])!;
    }
}

