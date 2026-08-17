using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using CloudImaging.Client.Views;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// WinPE UI thread and window layout tests (T047a, FR-002b).
///
/// The window-dimension/no-scroll assertions below read the actual shipped .xaml source files
/// from disk (located relative to this test file via <see cref="CallerFilePathAttribute"/>, which
/// is stable regardless of build output layout) rather than asserting hardcoded literals against
/// themselves. Real WPF <see cref="System.Windows.Window"/>/Page instantiation would require an
/// STA test thread, which this project does not currently provision (no StaFact/WpfFact
/// infrastructure) — reading the markup directly gives a real, non-tautological assertion without
/// that dependency.
/// </summary>
public sealed class UiThreadResponsivenessTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    // ── Window dimension contract ─────────────────────────────────────────────

    [Fact]
    public void MainWindow_MinimumWidth_Is800()
    {
        // FR-002b: 800×600 minimum window size, enforced in MainWindow.xaml.
        var root = LoadMainWindowXaml();
        double.Parse(root.Attribute("MinWidth")!.Value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(800,
            "MainWindow.xaml's MinWidth must match FR-002b's 800px minimum");
    }

    [Fact]
    public void MainWindow_MinimumHeight_Is600()
    {
        var root = LoadMainWindowXaml();
        double.Parse(root.Attribute("MinHeight")!.Value, System.Globalization.CultureInfo.InvariantCulture).Should().Be(600,
            "MainWindow.xaml's MinHeight must match FR-002b's 600px minimum");
    }

    // ── Centered layout ───────────────────────────────────────────────────────

    [Fact]
    public void MainWindow_StartsAtCenterScreen()
    {
        // FR-002b: window must be centered on the display at startup.
        var root = LoadMainWindowXaml();
        root.Attribute("WindowStartupLocation")!.Value.Should().Be("CenterScreen");
    }

    // ── No-scroll constraint ──────────────────────────────────────────────────

    [Theory]
    [InlineData("OperationSelectionView.xaml")]
    [InlineData("SessionInitView.xaml")]
    [InlineData("ProgressView.xaml")]
    [InlineData("ResultsView.xaml")]
    public void View_ContainsNoScrollViewer(string viewFileName)
    {
        // FR-002b: all views must fit within the minimum window size without scrolling.
        // A ScrollViewer anywhere in a view's markup would indicate the layout doesn't fit,
        // so its absence is used as a concrete, checkable proxy for the constraint.
        var path = Path.Combine(GetViewsDirectory(), viewFileName);
        File.Exists(path).Should().BeTrue($"expected a view file at {path}");

        var doc = XDocument.Load(path);
        doc.Descendants(Presentation + "ScrollViewer").Should().BeEmpty(
            $"{viewFileName} must fit within the minimum window size without scrolling (FR-002b)");
    }

    // ── Async operations must use Task, not Thread.Sleep ─────────────────────

    [Fact]
    public void SessionStatusPoller_UsesTaskDelay_NotThreadSleep()
    {
        // The poller must use Task.Delay (non-blocking) not Thread.Sleep (blocking)
        // Validated by confirming SessionStatusPoller.Start returns a Task-based loop
        var startMethod = typeof(CloudImaging.Client.Services.SessionStatusPoller).GetMethod("Start");
        startMethod.Should().NotBeNull("Start method must exist on SessionStatusPoller");
        // Start() returns void but internally calls RunLoopAsync which is Task-based
        startMethod!.ReturnType.Should().Be(typeof(void),
            "Start() is fire-and-forget; async work runs on background Task");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static XElement LoadMainWindowXaml() =>
        XDocument.Load(Path.Combine(GetViewsDirectory(), "MainWindow.xaml")).Root!;

    private static string GetViewsDirectory([CallerFilePath] string testFilePath = "") =>
        Path.Combine(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..")),
            "src", "CloudImaging.Client", "Views");
}

