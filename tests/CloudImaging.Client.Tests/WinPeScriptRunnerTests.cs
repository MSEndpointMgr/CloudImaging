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
/// Offline scripts: copied from their downloaded file (UTF-8 with BOM) to the applied volume rather
/// than the boot image's RAM disk, run with the applied drive in CI_WINDOWS_DRIVE through the
/// restricted runner, deleted afterwards, and every way a run can end gets the reason the timeline
/// shows.
/// </summary>
public sealed class WinPeScriptRunnerTests
{
    private static readonly string TextPath = SetupProfileContentLoader.TextPath("a");

    private static SetupScript Script(int timeout = 5) =>
        new() { Name = "Enable Wake on LAN", Runs = SetupScriptPhase.WinPE, Order = 1, TimeoutMinutes = timeout, ContentSha256 = "a" };

    private static (WinPeScriptRunner Runner, FakeRestrictedProcessRunner Processes, FakeSetupFileSystem Volume) Create(bool powerShell = true, string text = "Write-Output 'done'")
    {
        var volume = new FakeSetupFileSystem(@"W:\");
        volume.Files[TextPath] = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)];
        var processes = new FakeRestrictedProcessRunner();
        processes.ReadScript = path => volume.Files.TryGetValue(path[@"W:\".Length..], out var bytes) ? Convert.ToBase64String(bytes) : null;
        return (new WinPeScriptRunner(processes, volume, () => powerShell, NullLogger.Instance), processes, volume);
    }

    [Fact]
    public void Script_IsCopiedToTheWindowsVolume_RunWithTheDriveAndFolder_AndDeletedAfterwards()
    {
        var (runner, processes, volume) = Create(text: "Write-Output 'ä'");

        var outcome = runner.Run(Script(), TextPath, 3, "W:", CancellationToken.None);

        outcome.Outcome.Should().Be(StepItemOutcome.Succeeded);
        var (request, written) = processes.Runs.Single();
        request.FileName.Should().EndWith(@"WindowsPowerShell\v1.0\powershell.exe");
        request.Arguments.Should().Be(@"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""W:\Windows\Temp\CloudImaging\Scripts\0003.ps1""");
        request.WorkingDirectory.Should().Be(@"W:\Windows\Temp\CloudImaging\Scripts");
        request.Environment.Should().Contain("CI_WINDOWS_DRIVE", "W:");
        request.Timeout.Should().Be(TimeSpan.FromMinutes(5));
        Convert.FromBase64String(written!).Should().Equal([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Write-Output 'ä'")]);
        volume.Files.Keys.Should().Equal(TextPath);
        volume.Events.Should().Contain(@"delete Windows\Temp\CloudImaging\Scripts\0003.ps1");
    }

    [Fact]
    public void AScriptThatCannotBeWritten_Fails_AndNothingStarts()
    {
        var (runner, processes, volume) = Create();
        volume.FailWritesUnder = @"Windows\Temp\CloudImaging";

        var outcome = runner.Run(Script(), TextPath, 1, "W:", CancellationToken.None);

        outcome.Outcome.Should().Be(StepItemOutcome.Failed);
        outcome.Reason.Should().StartWith("The script could not be written to the Windows volume: ");
        processes.Runs.Should().BeEmpty();
    }

    [Fact]
    public void ExitCodeZero_Succeeds_WithDurationAndTheLastOutputLine()
    {
        var (runner, processes, _) = Create();
        processes.Respond = _ => new RestrictedProcessResult(RestrictedProcessOutcome.Exited, 0, TimeSpan.FromMilliseconds(4200), ["first", "  last line  ", ""]);

        var outcome = runner.Run(Script(), TextPath, 1, "W:", CancellationToken.None);

        outcome.Should().Be(new WinPeScriptOutcome(StepItemOutcome.Succeeded, null, 0, 4200, "last line"));
    }

    [Fact]
    public void AnyOtherExitCode_Fails()
    {
        var (runner, processes, _) = Create();
        processes.Respond = _ => new RestrictedProcessResult(RestrictedProcessOutcome.Exited, 1, TimeSpan.FromSeconds(4), [new string('x', 300)]);

        var outcome = runner.Run(Script(), TextPath, 1, "W:", CancellationToken.None);

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

        runner.Run(Script(timeout: minutes), TextPath, 1, "W:", CancellationToken.None).Reason.Should().Be(reason);
    }

    [Fact]
    public void WithoutPowerShell_TheScriptFails_AndNothingStarts()
    {
        var (runner, processes, _) = Create(powerShell: false);

        var outcome = runner.Run(Script(), TextPath, 1, "W:", CancellationToken.None);

        outcome.Reason.Should().Be("This boot image has no PowerShell, so the script cannot run. Generate a new boot image with Media Builder and update the USB media.");
        processes.Runs.Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheRestartGuard_TheScriptFails()
    {
        var (runner, processes, scratch) = Create();
        processes.Respond = _ => new RestrictedProcessResult(RestrictedProcessOutcome.GuardUnavailable, null, TimeSpan.Zero, []);

        var outcome = runner.Run(Script(), TextPath, 1, "W:", CancellationToken.None);

        outcome.Should().Be(new WinPeScriptOutcome(StepItemOutcome.Failed, "Not run because restarts could not be blocked on this device.", null, null, null));
        scratch.Files.Keys.Should().Equal(TextPath);
    }
}
