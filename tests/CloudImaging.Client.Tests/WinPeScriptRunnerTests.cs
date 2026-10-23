using System.Text;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Client.Tests.TestDoubles;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.SetupProfiles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// In WinPE scripts: written with a BOM so Windows PowerShell 5.1 reads them as UTF-8, run with the
/// applied drive in CI_WINDOWS_DRIVE through the restricted runner, deleted afterwards, and every
/// way a run can end gets the reason the timeline shows.
/// </summary>
public sealed class WinPeScriptRunnerTests
{
    private static SetupScript Script(string content = "Write-Output 'done'", int timeout = 5) =>
        new() { Name = "Enable Wake on LAN", Runs = SetupScriptPhase.WinPE, Order = 1, TimeoutMinutes = timeout, Content = content };

    private static (WinPeScriptRunner Runner, FakeRestrictedProcessRunner Processes, FakeSetupFileSystem Scratch) Create(bool powerShell = true)
    {
        var scratch = new FakeSetupFileSystem(@"X:\Windows\Temp\CloudImaging");
        var processes = new FakeRestrictedProcessRunner();
        processes.ReadScript = path => scratch.Files.TryGetValue(path[(@"X:\Windows\Temp\CloudImaging\").Length..], out var bytes) ? Convert.ToBase64String(bytes) : null;
        return (new WinPeScriptRunner(processes, scratch, () => powerShell, NullLogger.Instance), processes, scratch);
    }

    [Fact]
    public void Script_IsWrittenWithABom_RunWithTheDriveAndFolder_AndDeletedAfterwards()
    {
        var (runner, processes, scratch) = Create();

        var outcome = runner.Run(Script("Write-Output 'ä'"), 3, "W:", CancellationToken.None);

        outcome.Outcome.Should().Be(StepItemOutcome.Succeeded);
        var (request, written) = processes.Runs.Single();
        request.FileName.Should().EndWith(@"WindowsPowerShell\v1.0\powershell.exe");
        request.Arguments.Should().Be(@"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""X:\Windows\Temp\CloudImaging\Scripts\03.ps1""");
        request.WorkingDirectory.Should().Be(@"X:\Windows\Temp\CloudImaging\Scripts");
        request.Environment.Should().Contain("CI_WINDOWS_DRIVE", "W:");
        request.Timeout.Should().Be(TimeSpan.FromMinutes(5));
        Convert.FromBase64String(written!).Should().Equal([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Write-Output 'ä'")]);
        scratch.Files.Should().BeEmpty();
        scratch.Events.Should().Contain(@"delete Scripts\03.ps1");
    }

    [Fact]
    public void ExitCodeZero_Succeeds_WithDurationAndTheLastOutputLine()
    {
        var (runner, processes, _) = Create();
        processes.Respond = _ => new RestrictedProcessResult(RestrictedProcessOutcome.Exited, 0, TimeSpan.FromMilliseconds(4200), ["first", "  last line  ", ""]);

        var outcome = runner.Run(Script(), 1, "W:", CancellationToken.None);

        outcome.Should().Be(new WinPeScriptOutcome(StepItemOutcome.Succeeded, null, 0, 4200, "last line"));
    }

    [Fact]
    public void AnyOtherExitCode_Fails()
    {
        var (runner, processes, _) = Create();
        processes.Respond = _ => new RestrictedProcessResult(RestrictedProcessOutcome.Exited, 1, TimeSpan.FromSeconds(4), [new string('x', 300)]);

        var outcome = runner.Run(Script(), 1, "W:", CancellationToken.None);

        outcome.Outcome.Should().Be(StepItemOutcome.Failed);
        outcome.Reason.Should().Be("Exited with code 1.");
        outcome.ExitCode.Should().Be(1);
        outcome.LastOutputLine.Should().HaveLength(200);
    }

    [Theory]
    [InlineData(1, "Stopped after 1 minute.")]
    [InlineData(5, "Stopped after 5 minutes.")]
    public void ATimeout_FailsWithTheMinutes(int minutes, string reason)
    {
        var (runner, processes, _) = Create();
        processes.Respond = _ => new RestrictedProcessResult(RestrictedProcessOutcome.TimedOut, null, TimeSpan.FromMinutes(minutes), []);

        runner.Run(Script(timeout: minutes), 1, "W:", CancellationToken.None).Reason.Should().Be(reason);
    }

    [Fact]
    public void WithoutPowerShell_TheScriptFails_AndNothingStarts()
    {
        var (runner, processes, _) = Create(powerShell: false);

        var outcome = runner.Run(Script(), 1, "W:", CancellationToken.None);

        outcome.Reason.Should().Be("This boot image has no PowerShell, so the script cannot run. Generate a new boot image with Media Builder and update the USB media.");
        processes.Runs.Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheRestartGuard_TheScriptFails()
    {
        var (runner, processes, scratch) = Create();
        processes.Respond = _ => new RestrictedProcessResult(RestrictedProcessOutcome.GuardUnavailable, null, TimeSpan.Zero, []);

        var outcome = runner.Run(Script(), 1, "W:", CancellationToken.None);

        outcome.Should().Be(new WinPeScriptOutcome(StepItemOutcome.Failed, "Not run because restarts could not be blocked on this device.", null, null, null));
        scratch.Files.Should().BeEmpty();
    }
}
