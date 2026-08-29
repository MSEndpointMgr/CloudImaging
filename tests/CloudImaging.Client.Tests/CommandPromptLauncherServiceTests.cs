using System.Diagnostics;
using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Tests for <see cref="CommandPromptLauncherService"/> — the WinPE Client's "Command Prompt"
/// support tool (FR-051d), gated by the Media Builder's opt-in "Enable command prompt access"
/// checkbox (see <see cref="ViewModels.OperationSelectionViewModel.IsCommandPromptAvailable"/>).
///
/// A real (but instantly self-exiting) process is used in place of an actual interactive
/// cmd.exe window, so these tests never leave an open console behind in CI.
/// </summary>
public sealed class CommandPromptLauncherServiceTests
{
    [Fact]
    public void Launch_SetsTopmostFalse_ImmediatelyBeforeStartingProcess()
    {
        var topmostCalls = new List<bool>();
        var svc = new CommandPromptLauncherService(startProcessOverride: psi =>
        {
            psi.FileName.Should().Be("cmd.exe");
            psi.UseShellExecute.Should().BeFalse();
            return StartHarmlessExitingProcess();
        });

        svc.Launch(topmostCalls.Add);

        topmostCalls.Should().Contain(false, "Topmost must be dropped before/while the console is open so it isn't hidden behind the always-on-top Client");
    }

    [Fact]
    public async Task Launch_RestoresTopmostTrue_WhenProcessExits()
    {
        var topmostCalls = new List<bool>();
        var exited = new TaskCompletionSource();
        var svc = new CommandPromptLauncherService(startProcessOverride: psi => StartHarmlessExitingProcess());

        svc.Launch(value =>
        {
            topmostCalls.Add(value);
            if (value)
                exited.TrySetResult();
        });

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        topmostCalls.Should().ContainInOrder(false, true);
    }

    [Fact]
    public void Launch_RestoresTopmostTrue_WhenProcessFailsToStart()
    {
        var topmostCalls = new List<bool>();
        var svc = new CommandPromptLauncherService(startProcessOverride: _ => throw new InvalidOperationException("cmd.exe not found"));

        svc.Launch(topmostCalls.Add);

        topmostCalls.Should().ContainInOrder(false, true);
    }

    private static Process StartHarmlessExitingProcess() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow  = true,
        })!;
}
