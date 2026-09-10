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
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProgressViewModel.StepPercent)) raised++; };

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

    /// <summary>Formats a figure the way the view model does, so these assertions hold on build
    /// agents and workstations whose decimal separator is not a period.</summary>
    private static string Number(double value) => value.ToString("0.00", CultureInfo.CurrentCulture);
}
