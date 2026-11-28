using System.IO;
using System.Threading.Channels;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Format disk runs on the thread pool and shows diskpart's result lines in the activity list as
/// diskpart writes them, so a pause on the progress screen can be matched to the command running.
/// </summary>
public sealed class FormatStepActivityTests
{
    private const string Banner = "\r\nMicrosoft DiskPart version 10.0.26100.1\r\n\r\nCopyright (C) Microsoft Corporation.\r\nOn computer: MININT-4E2K9QF\r\n\r\n";

    private const string Output = Banner +
        "Disk 0 is now the selected disk.\r\n\r\n" +
        "DiskPart succeeded in cleaning the disk.\r\n\r\n" +
        "Please wait while DiskPart scans your configuration...\r\n\r\nDiskPart has finished scanning your configuration.\r\n\r\n" +
        "  0 percent completed\r  50 percent completed\r  100 percent completed\r\n\r\n" +
        "DiskPart successfully formatted the volume.\r\n";

    [Fact]
    public async Task OnlyResultLines_AreShown_InTheOrderDiskpartWroteThem()
    {
        var shown = new List<string>();

        await DiskFormatService.ReadDiskpartOutputAsync(new StringReader(Output), shown.Add, CancellationToken.None);

        shown.Should().Equal(
            "Disk 0 is now the selected disk.",
            "DiskPart succeeded in cleaning the disk.",
            "Please wait while DiskPart scans your configuration...",
            "DiskPart has finished scanning your configuration.",
            "DiskPart successfully formatted the volume.");
    }

    [Fact]
    public async Task TheWholeOutput_IsKeptForTheFailureMessage()
    {
        var transcript = await DiskFormatService.ReadDiskpartOutputAsync(new StringReader(Output), _ => { }, CancellationToken.None);

        transcript.Should().Contain("Microsoft DiskPart version").And.Contain("100 percent completed").And.Contain("DiskPart successfully formatted the volume.");
    }

    [Fact]
    public async Task EachLine_IsShownWhileDiskpartIsStillRunning()
    {
        var lines = Channel.CreateUnbounded<string>();
        foreach (var line in Banner.Split("\r\n").Append("Disk 0 is now the selected disk."))
        {
            lines.Writer.TryWrite(line);
        }

        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = DiskFormatService.ReadDiskpartOutputAsync(new ChannelLineReader(lines.Reader), line => first.TrySetResult(line), CancellationToken.None);

        (await first.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("Disk 0 is now the selected disk.");
        reading.IsCompleted.Should().BeFalse("diskpart has not finished yet");

        lines.Writer.Complete();
        await reading;
    }

    [Fact]
    public void ActivityLines_LoggedFromManyThreadsAtOnce_AreAllKept()
    {
        var progress = new ProgressViewModel();

        Parallel.For(0, 500, i => progress.AppendActivity($"line {i}"));

        progress.LogText.Split('\n').Should().HaveCount(30).And.OnlyHaveUniqueItems().And.OnlyContain(l => l.Contains("  line ", StringComparison.Ordinal));
    }

    private sealed class ChannelLineReader(ChannelReader<string> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await lines.WaitToReadAsync(cancellationToken) && lines.TryRead(out var line) ? line : null;
    }
}
