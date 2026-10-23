using System.IO;
using FluentAssertions;
using CloudImaging.Client.Services.Setup;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// The restricted runner against real processes: output and exit code come back, the timeout stops
/// the whole tree, and the child's token has neither shutdown privilege.
/// </summary>
public sealed class RestrictedProcessRunnerTests
{
    private static RestrictedProcessRequest Cmd(string arguments, int timeoutSeconds = 30, Dictionary<string, string>? environment = null) =>
        new(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments, Environment.SystemDirectory, environment ?? [], TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public void Run_ReturnsTheExitCode_AndTheMergedOutput_WithTheExtraEnvironment()
    {
        var result = new RestrictedProcessRunner().Run(Cmd("/c echo %CI_WINDOWS_DRIVE% & echo oops 1>&2 & exit 3", environment: new() { ["CI_WINDOWS_DRIVE"] = "W:" }), CancellationToken.None);

        result.Outcome.Should().Be(RestrictedProcessOutcome.Exited);
        result.ExitCode.Should().Be(3);
        result.Output.Select(l => l.Trim()).Should().Contain(["W:", "oops"]);
    }

    [Fact]
    public void Run_StopsTheProcessTree_AtTheTimeout()
    {
        var result = new RestrictedProcessRunner().Run(Cmd("/c ping -n 30 127.0.0.1", timeoutSeconds: 1), CancellationToken.None);

        result.Outcome.Should().Be(RestrictedProcessOutcome.TimedOut);
        result.ExitCode.Should().BeNull();
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Run_GivesTheChildNoShutdownPrivilege()
    {
        var result = new RestrictedProcessRunner().Run(Cmd("/c whoami /priv"), CancellationToken.None);

        result.Outcome.Should().Be(RestrictedProcessOutcome.Exited);
        result.Output.Should().NotContain(l => l.Contains("SeShutdownPrivilege", StringComparison.OrdinalIgnoreCase));
        result.Output.Should().NotContain(l => l.Contains("SeRemoteShutdownPrivilege", StringComparison.OrdinalIgnoreCase));
    }
}
