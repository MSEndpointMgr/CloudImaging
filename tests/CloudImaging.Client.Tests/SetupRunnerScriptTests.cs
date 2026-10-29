using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CloudImaging.Client.Services.Setup;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Runs the real setup runner under Windows PowerShell against a temporary folder that stands in
/// for the system drive: files are verified before anything starts, timeouts stop an item and the
/// next one still runs, everything staged is removed afterwards, and the script always exits 0
/// with one ASCII log line per item.
/// </summary>
public sealed partial class SetupRunnerScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ci-runner-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _logPath;
    private readonly string _manifestPath;

    public SetupRunnerScriptTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, SetupRunnerManifestBuilder.ScriptFolder));
        _logPath = Path.Combine(_root, @"Windows\Logs\CloudImaging\SetupRunner.log");
        _manifestPath = Path.Combine(_root, SetupRunnerManifestBuilder.ManifestPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ  \d+/\d+  (Script|DriverInstaller)  "".*""  (Succeeded|Failed)  .+$")]
    private static partial Regex ItemLine();

    private static string RunnerPath => Path.Combine(AppContext.BaseDirectory, "SetupRunner", UnattendComposer.RunnerScriptName);

    /// <summary>A file under the fake system drive, and its manifest entry.</summary>
    private SetupRunnerManifestFile Stage(string relativePath, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return new SetupRunnerManifestFile(relativePath, SetupRunnerManifestBuilder.Sha256(bytes));
    }

    private static SetupRunnerManifestItem CmdItem(int order, string name, string arguments, SetupRunnerManifestFile file, int timeoutSeconds = 60, IReadOnlyList<int>? success = null, IReadOnlyList<string>? cleanup = null) =>
        new(order, SetupRunnerItemKind.Script, name, [file], new SetupRunnerManifestCommand("cmd.exe", arguments, SetupRunnerManifestBuilder.ScriptFolder), timeoutSeconds, success ?? [0], cleanup ?? []);

    private void WriteManifest(params SetupRunnerManifestItem[] items)
    {
        var manifest = new SetupRunnerManifest(1, "2026-10-23T10:02:11Z", Guid.NewGuid(), new SetupRunnerManifestProfile("Stockholm", 8), items, [SetupRunnerManifestBuilder.StagingFolder]);
        File.WriteAllBytes(_manifestPath, SetupRunnerManifestBuilder.Serialize(manifest));
    }

    private (int ExitCode, string[] Log) Run()
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // -Command rather than -File: on the GitHub Windows Server 2025 runners, powershell.exe -File exits 0
        // without running the script at all, even a trivial one, while -Command runs it normally.
        var command = $"& '{RunnerPath.Replace("'", "''")}' -ManifestPath '{_manifestPath.Replace("'", "''")}' -LogPath '{_logPath.Replace("'", "''")}'; exit $LASTEXITCODE";
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["SystemDrive"] = _root;
        using var process = Process.Start(start)!;
        var stdErr = process.StandardError.ReadToEndAsync();
        var stdOut = process.StandardOutput.ReadToEnd();
        process.WaitForExit(120_000).Should().BeTrue();

        // Every run logs at least a Start or End line; without one, the runner's own output says why.
        File.Exists(_logPath).Should().BeTrue($"the runner always writes its log. Exit {process.ExitCode}. Output: {stdOut} {stdErr.Result}");
        return (process.ExitCode, File.ReadAllLines(_logPath));
    }

    [Fact]
    public void AMatchingItem_Runs_AndIsLoggedSucceeded()
    {
        var marker = Path.Combine(_root, "ran.txt");
        WriteManifest(CmdItem(1, "Wired 802.1X", $"/c echo x> \"{marker}\" & rem {{0}}", Stage(SetupRunnerManifestBuilder.ScriptPath(1), "x")));

        var (exitCode, log) = Run();

        exitCode.Should().Be(0);
        File.Exists(marker).Should().BeTrue();
        log.Should().HaveCount(3);
        log[0].Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ  Start  Stockholm, version 8  1 item$");
        log[1].Should().MatchRegex(ItemLine().ToString()).And.Contain("  1/1  Script  \"Wired 802.1X\"  Succeeded  exit 0  ").And.MatchRegex(@"  \d+\.\d s$");
        log[2].Should().EndWith("  End  Stockholm, version 8  1 succeeded, 0 failed");
        string.Join('\n', log).Should().MatchRegex("^[\\x00-\\x7F]*$");
    }

    [Fact]
    public void AChangedFile_IsNotRun_AndLaterItemsStillRun()
    {
        var marker = Path.Combine(_root, "ran.txt");
        var file = Stage(SetupRunnerManifestBuilder.ScriptPath(1), "original");
        File.WriteAllText(Path.Combine(_root, file.Path), "tampered");
        WriteManifest(
            CmdItem(1, "Tampered", $"/c echo x> \"{marker}\"", file),
            CmdItem(2, "Next", "/c exit 0", Stage(SetupRunnerManifestBuilder.ScriptPath(2), "y")));

        var (exitCode, log) = Run();

        exitCode.Should().Be(0);
        File.Exists(marker).Should().BeFalse();
        log.Should().Contain(l => l.Contains("\"Tampered\"  Failed  File does not match the manifest", StringComparison.Ordinal));
        log.Should().Contain(l => l.Contains("\"Next\"  Succeeded  exit 0", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingFile_IsNotRun()
    {
        WriteManifest(CmdItem(1, "Missing", "/c exit 0", new SetupRunnerManifestFile(@"Windows\Setup\CloudImaging\Scripts\09.ps1", "00")));

        Run().Log.Should().Contain(l => l.Contains("\"Missing\"  Failed  File does not match the manifest", StringComparison.Ordinal)
            && l.EndsWith("  0.0 s", StringComparison.Ordinal) && l.Split("  ").Length == 7);
    }

    [Fact]
    public void ATimeout_StopsTheItem_AndTheNextItemRuns()
    {
        WriteManifest(
            CmdItem(1, "Slow", "/c ping -n 30 127.0.0.1 > nul", Stage(SetupRunnerManifestBuilder.ScriptPath(1), "a"), timeoutSeconds: 1),
            CmdItem(2, "Next", "/c exit 0", Stage(SetupRunnerManifestBuilder.ScriptPath(2), "b")));

        var (_, log) = Run();

        log.Should().Contain(l => l.Contains("\"Slow\"  Failed  Stopped after 1 minute", StringComparison.Ordinal));
        log.Should().Contain(l => l.Contains("\"Next\"  Succeeded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3010)]
    [InlineData(1641)]
    public void ANonZeroExitCode_FailsAScript_AndDoesNotStopTheNextOne(int code)
    {
        WriteManifest(
            CmdItem(1, "Bad", $"/c exit {code}", Stage(SetupRunnerManifestBuilder.ScriptPath(1), "a")),
            CmdItem(2, "Next", "/c exit 0", Stage(SetupRunnerManifestBuilder.ScriptPath(2), "b")));

        var (exitCode, log) = Run();

        exitCode.Should().Be(0);
        log.Should().Contain(l => l.Contains($"\"Bad\"  Failed  exit {code}", StringComparison.Ordinal));
        log.Should().Contain(l => l.Contains("\"Next\"  Succeeded", StringComparison.Ordinal));
    }

    [Fact]
    public void APathThatLeavesTheSystemDrive_IsSkipped()
    {
        WriteManifest(CmdItem(1, "Escape", "/c exit 0", new SetupRunnerManifestFile(@"..\outside.txt", "00")));

        Run().Log.Should().Contain(l => l.Contains("\"Escape\"  Failed  Skipped because a path leaves the system drive", StringComparison.Ordinal));
    }

    [Fact]
    public void Cleanup_RemovesEveryStagedPath_AfterAFailureToo_ButNeverTheLog()
    {
        var driverFolder = @"CloudImaging\Drivers\sp1";
        var driverFile = Stage($@"{driverFolder}\setup.cmd", "@exit /b 1");
        WriteManifest(new SetupRunnerManifestItem(1, SetupRunnerItemKind.DriverInstaller, "HP Hotkey Support", [driverFile],
            new SetupRunnerManifestCommand("cmd.exe", "/c exit 1", driverFolder), 60, [0, 3010], [driverFolder]));

        var (exitCode, log) = Run();

        exitCode.Should().Be(0);
        log.Should().Contain(l => l.Contains("1/1  DriverInstaller  \"HP Hotkey Support\"  Failed  exit 1", StringComparison.Ordinal));
        Directory.Exists(Path.Combine(_root, driverFolder)).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, SetupRunnerManifestBuilder.StagingFolder)).Should().BeFalse();
        File.Exists(_logPath).Should().BeTrue();
    }

    [Fact]
    public void Cleanup_RemovesWhatAnInstallerExtractedItself_AndStagingFoldersLeftEmpty_ButNotOtherContentOrWindowsFolders()
    {
        var driverFolder = @"CloudImaging\Drivers\000-sp1";
        var driverFile = Stage($@"{driverFolder}\sp1.exe", "@exit /b 0");
        Stage(@"SWSetup\SP1\setup.exe", "extracted by the SoftPaq");
        Stage(@"SWSetup\SP9\keep.txt", "already on the image");
        WriteManifest(new SetupRunnerManifestItem(1, SetupRunnerItemKind.DriverInstaller, "HP Hotkey Support", [driverFile],
            new SetupRunnerManifestCommand("cmd.exe", "/c exit 0", driverFolder), 60, [0, 3010], [driverFolder, @"SWSetup\sp1"]));

        Run();

        Directory.Exists(Path.Combine(_root, "SWSetup", "SP1")).Should().BeFalse();
        File.Exists(Path.Combine(_root, @"SWSetup\SP9\keep.txt")).Should().BeTrue("only the component's own folder is removed");
        Directory.Exists(Path.Combine(_root, "CloudImaging")).Should().BeFalse("the staging folders are empty afterwards");
        Directory.Exists(Path.Combine(_root, @"Windows\Setup")).Should().BeTrue("folders under Windows are never removed");
    }

    /// <summary>A driver installer as Install drivers hands it over: a staged folder with a command script that appends to <paramref name="order"/>.</summary>
    private SpecializeInstaller DriverInstaller(string name, string folder, string order, string mark, int exitCode)
    {
        var script = Stage($@"{folder}\setup.cmd", $"@echo off\r\n(echo {mark})>>\"{order}\"\r\nexit /b {exitCode}\r\n");
        var dll = Stage($@"{folder}\driver.dll", "payload");
        return new SpecializeInstaller(
            name,
            [new SpecializeInstallerFile(script.Path, script.Sha256), new SpecializeInstallerFile(dll.Path, dll.Sha256)],
            new SpecializeInstallerCommand("cmd.exe", "/c setup.cmd", folder),
            1200,
            [0, 3010],
            [folder]);
    }

    /// <summary>The manifest Prepare setup writes, with the profile script started through cmd.exe, since the stand-in drive has no Windows folder.</summary>
    private void WriteBuiltManifest(IReadOnlyList<SpecializeInstaller> installers, string order)
    {
        var script = Stage(SetupRunnerManifestBuilder.ScriptPath(1), "Write-Output 'x'");
        var built = SetupRunnerManifestBuilder.Build(Guid.NewGuid(), new DateTimeOffset(2026, 10, 23, 10, 2, 11, TimeSpan.Zero), "Stockholm", 8, installers, [new StagedScript("Wired 802.1X", script.Path, script.Sha256, 1)]);
        var items = built.Items.Select(i => i.Kind == SetupRunnerItemKind.Script
            ? i with { Command = new SetupRunnerManifestCommand("cmd.exe", $"/c (echo script)>>\"{order}\"", SetupRunnerManifestBuilder.ScriptFolder) }
            : i).ToList();
        File.WriteAllBytes(_manifestPath, SetupRunnerManifestBuilder.Serialize(built with { Items = items }));
    }

    [Fact]
    public void DriverInstallers_RunFirstInOrder_3010Succeeds_AFailureDoesNotStopTheNext_AndTheirFoldersAreDeleted()
    {
        var order = Path.Combine(_root, "order.txt");
        var first = DriverInstaller("HP Audio Control", @"CloudImaging\Drivers\000-sp1", order, "first", 3010);
        var second = DriverInstaller("HP Hotkey Support", @"CloudImaging\Drivers\001-sp2", order, "second", 1);
        WriteBuiltManifest([first, second], order);

        var (exitCode, log) = Run();

        exitCode.Should().Be(0, "the runner never asks Windows setup for a restart");
        File.ReadAllLines(order).Select(l => l.Trim()).Should().Equal("first", "second", "script");
        log.Should().Contain(l => l.Contains("1/3  DriverInstaller  \"HP Audio Control\"  Succeeded  exit 3010", StringComparison.Ordinal));
        log.Should().Contain(l => l.Contains("2/3  DriverInstaller  \"HP Hotkey Support\"  Failed  exit 1", StringComparison.Ordinal));
        log.Should().Contain(l => l.Contains("3/3  Script  \"Wired 802.1X\"  Succeeded", StringComparison.Ordinal));
        Directory.Exists(Path.Combine(_root, @"CloudImaging\Drivers\000-sp1")).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, @"CloudImaging\Drivers\001-sp2")).Should().BeFalse("folders are deleted after a failure too");

        // Results go only to the local log: nothing else is written besides the marker this test asked for.
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_root, f)).Should().BeEquivalentTo(@"Windows\Logs\CloudImaging\SetupRunner.log", "order.txt");
    }

    [Fact]
    public void AChangedStagedInstallerFile_IsRefused_TheNextItemRuns_AndTheFolderIsStillDeleted()
    {
        var order = Path.Combine(_root, "order.txt");
        var tampered = DriverInstaller("HP Audio Control", @"CloudImaging\Drivers\000-sp1", order, "first", 0);
        var second = DriverInstaller("HP Hotkey Support", @"CloudImaging\Drivers\001-sp2", order, "second", 0);
        File.WriteAllText(Path.Combine(_root, @"CloudImaging\Drivers\000-sp1\driver.dll"), "changed");
        WriteBuiltManifest([tampered, second], order);

        var (_, log) = Run();

        log.Should().Contain(l => l.Contains("1/3  DriverInstaller  \"HP Audio Control\"  Failed  File does not match the manifest", StringComparison.Ordinal));
        File.ReadAllLines(order).Select(l => l.Trim()).Should().Equal("second", "script");
        Directory.Exists(Path.Combine(_root, @"CloudImaging\Drivers\000-sp1")).Should().BeFalse();
    }

    [Fact]
    public void AMissingManifest_LogsOneLine_CleansUp_AndExitsZero()
    {
        Stage(SetupRunnerManifestBuilder.ScriptPath(1), "leftover");

        var (exitCode, log) = Run();

        exitCode.Should().Be(0);
        log.Should().ContainSingle().Which.Should().Contain("No setup profile");
        Directory.Exists(Path.Combine(_root, SetupRunnerManifestBuilder.StagingFolder)).Should().BeFalse();
    }

    [Fact]
    public void AnUnreadableManifest_ExitsZero()
    {
        File.WriteAllText(_manifestPath, "{ not json");

        Run().ExitCode.Should().Be(0);
    }

    [Fact]
    public void TheScript_FollowsTheRepositoryRules()
    {
        var text = File.ReadAllText(RunnerPath);

        text.Should().NotContain("Write-Host");
        text.Should().NotContain(" += ");
        text.Split('\n').Should().NotContain(l => l.TrimEnd('\r').EndsWith('`'));
        text.Should().NotContain("#Requires");
    }
}
