using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

public sealed class SystemRestartServiceTests
{
    [Fact]
    public void RestartCommand_UsesWindowsPeUtility()
    {
        var startInfo = SystemRestartService.CreateRestartProcessStartInfo();

        startInfo.FileName.Should().Be("wpeutil.exe");
        startInfo.Arguments.Should().Be("Reboot");
        startInfo.UseShellExecute.Should().BeFalse();
        startInfo.CreateNoWindow.Should().BeTrue();
    }
}