using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Tests for the guard that refuses to let destructive imaging operations (format/download/apply)
/// run outside a genuine WinPE boot environment.
/// </summary>
/// <remarks>
/// These tests run on a normal Windows dev/CI machine, which is never WinPE — the MiniNT registry
/// key is never present there, so <see cref="WinPeEnvironmentGuard.IsWinPe"/> is expected to
/// deterministically return <c>false</c> and <see cref="WinPeEnvironmentGuard.EnsureRunningInWinPe"/>
/// is expected to deterministically throw, on every machine this test suite runs on (including CI,
/// which never runs inside WinPE either).
/// </remarks>
public sealed class WinPeEnvironmentGuardTests
{
    [Fact]
    public void IsWinPe_IsFalse_OnANormalWindowsMachine()
    {
        WinPeEnvironmentGuard.IsWinPe().Should().BeFalse(
            "the test/CI machine this runs on is a normal Windows install, not WinPE");
    }

    [Fact]
    public void EnsureRunningInWinPe_Throws_OnANormalWindowsMachine()
    {
        var act = () => WinPeEnvironmentGuard.EnsureRunningInWinPe("Formatting the target disk");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Formatting the target disk*WinPE*",
                "the message must name the refused action and explain why (not running in WinPE)");
    }
}
