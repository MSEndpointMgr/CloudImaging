using CloudImaging.Client.Logging;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Past 10 MB Serilog continues in a numbered file. The log viewer and the failure upload must
/// follow it, or they stop at the first 10 MB while the session goes on.
/// </summary>
public sealed class LoggingConfigurationTests
{
    [Fact]
    public void TheNewestFileOfTheDay_IsTheHighestNumberedOne()
    {
        string[] names =
        [
            "cloud-imaging-client-20261114.log",
            "cloud-imaging-client-20261114_009.log",
            "cloud-imaging-client-20261114_010.log",
            "cloud-imaging-client-20261114_002.log",
        ];

        LoggingConfiguration.Newest(names).Should().Be("cloud-imaging-client-20261114_010.log");
    }

    [Fact]
    public void BeforeTheFirstRollover_ThePlainDailyFileIsTheNewest()
    {
        LoggingConfiguration.Newest(["cloud-imaging-client-20261114.log"]).Should().Be("cloud-imaging-client-20261114.log");
        LoggingConfiguration.Newest([]).Should().BeNull();
    }
}
