using System.Text;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Client.Tests.TestDoubles;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// The Prepare setup step's registry and answer file phases: hives load only when needed and are
/// unloaded before the answer file is written, a missing hive fails only its own values, the
/// answer file always replaces any other, and the two failure handling settings decide the result.
/// </summary>
public sealed class PrepareSetupServiceTests
{
    private static SetupRegistryValue Value(string name, SetupRegistryHive hive, string key = @"Policies\Test", string data = "1") =>
        new() { Name = name, Hive = hive, Key = key, ValueName = name.Replace(' ', '_'), Type = SetupRegistryValueType.DWord, Data = data };

    private static (PrepareSetupService Service, FakeOfflineRegistryHive Hive, FakeSetupFileSystem Files, List<(string Line, int Percent)> Progress) Create()
    {
        var hive = new FakeOfflineRegistryHive();
        var files = new FakeSetupFileSystem();
        return (new PrepareSetupService(hive, files, new StubScriptRunner(), () => RunnerBytes, TimeProvider.System, NullLogger.Instance), hive, files, []);
    }

    private static readonly byte[] RunnerBytes = Encoding.ASCII.GetBytes("# runner");

    /// <summary>Answers every script from a function, so the step's ordering and stop rules are tested on their own.</summary>
    private sealed class StubScriptRunner : IWinPeScriptRunner
    {
        public List<(string Name, int Position, string Drive)> Runs { get; } = [];

        public List<string> TextPaths { get; } = [];

        public Func<SetupScript, WinPeScriptOutcome> Respond { get; set; } = _ => new WinPeScriptOutcome(StepItemOutcome.Succeeded, null, 0, 1200, "done");

        public WinPeScriptOutcome Run(SetupScript script, string textPath, int position, string windowsDrive, CancellationToken cancellationToken)
        {
            Runs.Add((script.Name, position, windowsDrive));
            TextPaths.Add(textPath);
            return Respond(script);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Every script text a test used, by hash.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Texts = new(StringComparer.Ordinal);

    private static readonly byte[] Bom = Encoding.UTF8.GetPreamble();

    private FakeSetupFileSystem _files = new();

    private static SetupScript Script(string name, SetupScriptPhase runs, int order, string content = "exit 0", int timeout = 5)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var sha = SetupRunnerManifestBuilder.Sha256(bytes);
        Texts[sha] = bytes;
        return new() { Name = name, Runs = runs, Order = order, TimeoutMinutes = timeout, ContentSha256 = sha, SizeBytes = bytes.Length };
    }

    private static SpecializeInstaller Installer(string name, string file) =>
        new(name, [new SpecializeInstallerFile(file, "ab")], new SpecializeInstallerCommand(file, "/s /norestart", @"CloudImaging\Drivers"), 1200, [0, 3010], [@"CloudImaging\Drivers"]);

    private static PrepareSetupResult Run(PrepareSetupService service, List<(string, int)> progress, SetupProfileContent content) =>
        service.Run(new PrepareSetupInput("Stockholm", content, [], @"W:\", MachineArchitecture.X64), (line, percent) => progress.Add((line, percent)));

    [Fact]
    public void Values_AreWrittenPerHive_AndEachHiveIsUnloadedBeforeTheAnswerFile()
    {
        var (service, hive, files, progress) = Create();
        var content = new SetupProfileContent
        {
            RegistryValues = [Value("Default user value", SetupRegistryHive.DefaultUser), Value("Software value", SetupRegistryHive.Software)],
        };

        var result = Run(service, progress, content);

        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.WarningCount.Should().Be(0);
        hive.Events.Should().ContainInOrder("load Software", @"set Software Policies\Test\Software_value", "unload Software", "load System", "unload System", "load DefaultUser", @"set DefaultUser Policies\Test\Default_user_value", "unload DefaultUser");
        hive.Loaded.Should().BeEmpty("every hive is unloaded before the next phase");
        files.Events.Should().Contain(@"write Windows\Panther\unattend.xml");
        result.Items.Select(i => i.Name).Should().Equal("Default user value", "Software value", "Answer file");
    }

    [Fact]
    public void ClearsUnattendFile_InTheOfflineSystemHive()
    {
        var (service, hive, _, progress) = Create();
        hive.Values[(SetupRegistryHive.System, "Setup", "UnattendFile")] = @"C:\old.xml";

        Run(service, progress, new SetupProfileContent());

        hive.Values.Should().NotContainKey((SetupRegistryHive.System, "Setup", "UnattendFile"));
    }

    [Fact]
    public void CurrentControlSet_IsWrittenToTheImagesControlSet()
    {
        var (service, hive, _, progress) = Create();
        hive.Values[(SetupRegistryHive.System, "Select", "Current")] = 2;

        Run(service, progress, new SetupProfileContent { RegistryValues = [Value("Time zone", SetupRegistryHive.System, @"CurrentControlSet\Services\tzautoupdate")] });

        hive.Values.Should().ContainKey((SetupRegistryHive.System, @"ControlSet002\Services\tzautoupdate", "Time_zone"));
    }

    [Fact]
    public void AMissingHive_FailsItsValues_AndOtherHivesContinue()
    {
        var (service, hive, _, progress) = Create();
        hive.LoadFailures[SetupRegistryHive.DefaultUser] = "The default user profile was not found in the applied image.";
        var content = new SetupProfileContent { RegistryValues = [Value("User value", SetupRegistryHive.DefaultUser), Value("Machine value", SetupRegistryHive.Software)] };

        var result = Run(service, progress, content);

        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.WarningCount.Should().Be(1);
        var user = result.Items.Single(i => i.Name == "User value");
        user.Outcome.Should().Be(StepItemOutcome.Failed);
        user.Reason.Should().Be("The default user profile was not found in the applied image.");
        result.Items.Single(i => i.Name == "Machine value").Outcome.Should().Be(StepItemOutcome.Succeeded);
    }

    [Fact]
    public void AnswerFile_ReplacesOtherAnswerFiles()
    {
        var (service, _, files, progress) = Create();
        files.Files[@"Windows\Panther\unattend.xml"] = Encoding.UTF8.GetBytes("<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"><old/></unattend>");
        files.Files[@"Windows\Panther\Unattend\unattend.xml"] = [1];
        files.Files[@"Windows\Panther\Unattend\autounattend.xml"] = [1];

        var result = Run(service, progress, new SetupProfileContent());

        result.Items.Single().Should().Match<ImagingStepItem>(i => i.Kind == StepItemKind.AnswerFile && i.Outcome == StepItemOutcome.Succeeded);
        files.Files.Should().NotContainKey(@"Windows\Panther\Unattend\unattend.xml");
        files.Files.Should().NotContainKey(@"Windows\Panther\Unattend\autounattend.xml");
        Encoding.UTF8.GetString(files.Files[@"Windows\Panther\unattend.xml"]).Should().NotContain("old");
        files.Events.IndexOf(@"delete Windows\Panther\Unattend\unattend.xml").Should().BeLessThan(files.Events.IndexOf(@"write Windows\Panther\unattend.xml"));
    }

    [Fact]
    public void CompleteWithWarnings_ReportsCompletedWithTheFailedCount()
    {
        var (service, hive, _, progress) = Create();
        hive.SetFailure = (_, _, name) => name == "Bad" ? new UnauthorizedAccessException("Access is denied.") : null;
        var content = new SetupProfileContent { RegistryValues = [Value("Bad", SetupRegistryHive.Software), Value("Good", SetupRegistryHive.Software)] };

        var result = Run(service, progress, content);

        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.WarningCount.Should().Be(1);
        result.ErrorDetail.Should().BeNull();
        result.Items.Single(i => i.Name == "Good").Outcome.Should().Be(StepItemOutcome.Succeeded);
    }

    [Fact]
    public void FailTheDeployment_StopsAtTheFirstFailure()
    {
        var (service, hive, _, progress) = Create();
        hive.SetFailure = (_, _, name) => name == "Bad" ? new UnauthorizedAccessException("Access is denied.") : null;
        var content = new SetupProfileContent
        {
            FailureHandling = SetupFailureHandling.FailDeployment,
            RegistryValues = [Value("Bad", SetupRegistryHive.Software), Value("Later", SetupRegistryHive.Software), Value("Much later", SetupRegistryHive.DefaultUser)],
        };

        var result = Run(service, progress, content);

        result.Status.Should().Be(ImagingStepStatus.Failed);
        result.ErrorDetail.Should().Be("Bad: The value could not be written: Access is denied.");
        result.Items.Where(i => i.Name != "Bad").Should().OnlyContain(i => i.Outcome == StepItemOutcome.NotRun
            && i.Reason == "Not run because an earlier item failed and this profile fails the deployment.");
        hive.Events.Should().NotContain("load DefaultUser");
    }

    [Fact]
    public void ErrorDetail_IsAtMost500Characters()
    {
        var (service, hive, _, progress) = Create();
        hive.SetFailure = (_, _, _) => new UnauthorizedAccessException(new string('x', 800));
        var content = new SetupProfileContent { FailureHandling = SetupFailureHandling.FailDeployment, RegistryValues = [Value("Bad", SetupRegistryHive.Software)] };

        Run(service, progress, content).ErrorDetail!.Length.Should().Be(500);
    }

    [Fact]
    public void StatusLines_AndPercent_FollowTheItems()
    {
        var (service, _, _, progress) = Create();
        var content = new SetupProfileContent { RegistryValues = [Value("A", SetupRegistryHive.Software), Value("B", SetupRegistryHive.Software), Value("C", SetupRegistryHive.Software)] };

        Run(service, progress, content);

        progress.Select(p => p.Line).Distinct().Should().Equal("Writing registry values", "Writing the answer file");
        progress.Where(p => p.Line == "Writing registry values").Select(p => p.Percent).Should().Equal(0, 25, 50, 75);
        progress[^1].Percent.Should().Be(100);
    }

    private (PrepareSetupService Service, StubScriptRunner Scripts, FakeOfflineRegistryHive Hive, FakeSetupFileSystem Files) CreateWithScripts(bool generalized = true)
    {
        var hive = new FakeOfflineRegistryHive();
        if (generalized)
        {
            hive.Values[(SetupRegistryHive.System, @"Setup\Status\SysprepStatus", "GeneralizationState")] = 7;
        }
        var files = _files = new FakeSetupFileSystem();
        var scripts = new StubScriptRunner();
        var time = new FixedTime(new DateTimeOffset(2026, 10, 23, 10, 2, 11, TimeSpan.Zero));
        return (new PrepareSetupService(hive, files, scripts, () => RunnerBytes, time, NullLogger.Instance), scripts, hive, files);
    }

    /// <summary>Runs the step with every known script text already downloaded to the volume, as the loader leaves them.</summary>
    private PrepareSetupResult RunWith(PrepareSetupService service, SetupProfileContent? content, IReadOnlyList<SpecializeInstaller>? installers = null, List<(string, int)>? progress = null, string? answerFile = null)
    {
        var scriptFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sha in (content?.Scripts ?? []).Select(s => s.ContentSha256).Where(Texts.ContainsKey))
        {
            scriptFiles[sha] = SetupProfileContentLoader.TextPath(sha);
            _files.Files[scriptFiles[sha]] = [.. Bom, .. Texts[sha]];
        }
        return service.Run(
            new PrepareSetupInput(content is null ? null : "Stockholm", content, installers ?? [], @"W:\", MachineArchitecture.X64)
            {
                SessionId = Guid.Parse("5b7e0f0e-6a0c-4b8e-9a0d-1c2b3d4e5f60"),
                ProfileVersion = content is null ? null : 8,
                ScriptFiles = scriptFiles,
                AnswerFileText = answerFile,
            },
            (line, percent) => progress?.Add((line, percent)));
    }

    [Fact]
    public void WinPeScripts_RunInProfileOrder_AfterTheHivesAreUnloaded_WithTheAppliedDrive()
    {
        var (service, scripts, hive, _) = CreateWithScripts();
        var content = new SetupProfileContent
        {
            RegistryValues = [Value("Machine value", SetupRegistryHive.Software)],
            Scripts = [Script("Second", SetupScriptPhase.WinPE, 2), Script("Setup only", SetupScriptPhase.WindowsSetup, 1), Script("First", SetupScriptPhase.WinPE, 1)],
        };

        var result = RunWith(service, content);

        scripts.Runs.Should().Equal(("First", 1, "W:"), ("Second", 2, "W:"));
        hive.Loaded.Should().BeEmpty();
        result.Items.Select(i => (i.Kind, i.Name)).Should().Equal(
            (StepItemKind.RegistryValue, "Machine value"),
            (StepItemKind.WinPEScript, "First"),
            (StepItemKind.WinPEScript, "Second"),
            (StepItemKind.WindowsSetupScript, "Setup only"),
            (StepItemKind.AnswerFile, "Answer file"));
        var first = result.Items.Single(i => i.Name == "First");
        first.ExitCode.Should().Be(0);
        first.DurationMs.Should().Be(1200);
        first.LastOutputLine.Should().Be("done");
    }

    [Fact]
    public void AFailedWinPeScript_UnderCompleteWithWarnings_LetsTheNextScriptRun()
    {
        var (service, scripts, _, _) = CreateWithScripts();
        scripts.Respond = s => s.Name == "Bad"
            ? new WinPeScriptOutcome(StepItemOutcome.Failed, WinPeScriptRunner.StoppedAfter(1), null, 60_000, null)
            : new WinPeScriptOutcome(StepItemOutcome.Succeeded, null, 0, 10, null);
        var content = new SetupProfileContent { Scripts = [Script("Bad", SetupScriptPhase.WinPE, 1, timeout: 1), Script("Next", SetupScriptPhase.WinPE, 2)] };

        var result = RunWith(service, content);

        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.WarningCount.Should().Be(1);
        result.Items.Single(i => i.Name == "Bad").Reason.Should().Be("Stopped after 1 minute.");
        scripts.Runs.Select(r => r.Name).Should().Equal("Bad", "Next");
    }

    [Fact]
    public void AFailedWinPeScript_UnderFailTheDeployment_StopsEverythingAfterIt()
    {
        var (service, scripts, _, files) = CreateWithScripts();
        scripts.Respond = s => s.Name == "Bad" ? new WinPeScriptOutcome(StepItemOutcome.Failed, "Exited with code 1.", 1, 10, null) : new WinPeScriptOutcome(StepItemOutcome.Succeeded, null, 0, 10, null);
        var content = new SetupProfileContent
        {
            FailureHandling = SetupFailureHandling.FailDeployment,
            Scripts = [Script("Bad", SetupScriptPhase.WinPE, 1), Script("Later", SetupScriptPhase.WinPE, 2), Script("Setup", SetupScriptPhase.WindowsSetup, 1)],
        };

        var result = RunWith(service, content);

        result.Status.Should().Be(ImagingStepStatus.Failed);
        result.ErrorDetail.Should().Be("Bad: Exited with code 1.");
        scripts.Runs.Select(r => r.Name).Should().Equal("Bad");
        result.Items.Where(i => i.Name != "Bad").Should().OnlyContain(i => i.Outcome == StepItemOutcome.NotRun);
        files.Files.Keys.Should().NotContain(k => k.StartsWith(@"Windows\Setup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WindowsSetupWork_StagesTheRunnerScriptsAndManifest_AndTheAnswerFileGetsOneRunnerCommand()
    {
        var (service, _, _, files) = CreateWithScripts();
        files.Files[@"CloudImaging\Drivers\sp1\setup.exe"] = [1];
        var content = new SetupProfileContent { Scripts = [Script("Wired 802.1X", SetupScriptPhase.WindowsSetup, 1, "Write-Output 'x'"), Script("Second", SetupScriptPhase.WindowsSetup, 2)] };

        var result = RunWith(service, content, [Installer("HP Hotkey Support", @"CloudImaging\Drivers\sp1\setup.exe")]);

        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.Items.Select(i => (i.Kind, i.Name, i.Outcome)).Should().Equal(
            (StepItemKind.DriverInstaller, "HP Hotkey Support", StepItemOutcome.Queued),
            (StepItemKind.WindowsSetupScript, "Wired 802.1X", StepItemOutcome.Queued),
            (StepItemKind.WindowsSetupScript, "Second", StepItemOutcome.Queued),
            (StepItemKind.AnswerFile, "Answer file", StepItemOutcome.Succeeded));
        files.Files[@"Windows\Setup\CloudImaging\Invoke-CloudImagingSetup.ps1"].Should().Equal(RunnerBytes);
        files.Files[@"Windows\Setup\CloudImaging\Scripts\0001.ps1"].Should().Equal([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Write-Output 'x'")]);
        files.Files.Keys.Should().Contain(@"Windows\Setup\CloudImaging\Scripts\0002.ps1");
        var manifest = Encoding.UTF8.GetString(files.Files[@"Windows\Setup\CloudImaging\manifest.json"]);
        manifest.Should().Contain("\"name\": \"Stockholm\"").And.Contain("\"version\": 8").And.Contain("5b7e0f0e-6a0c-4b8e-9a0d-1c2b3d4e5f60").And.Contain("2026-10-23T10:02:11Z");
        manifest.IndexOf("HP Hotkey Support", StringComparison.Ordinal).Should().BeLessThan(manifest.IndexOf("Wired 802.1X", StringComparison.Ordinal));
        var answerFile = UnattendComposer.Parse(Encoding.UTF8.GetString(files.Files[PrepareSetupService.AnswerFilePath]));
        UnattendComposer.CountRunnerCommands(answerFile).Should().Be(1);
    }

    [Fact]
    public void AMissingInstallerFile_FailsThatInstaller_AndTheRestIsStillStaged()
    {
        var (service, _, _, files) = CreateWithScripts();
        var content = new SetupProfileContent { Scripts = [Script("Setup", SetupScriptPhase.WindowsSetup, 1)] };

        var result = RunWith(service, content, [Installer("Realtek Audio Console", @"CloudImaging\Drivers\sp2\setup.exe")]);

        var installer = result.Items.Single(i => i.Kind == StepItemKind.DriverInstaller);
        installer.Outcome.Should().Be(StepItemOutcome.Failed);
        installer.Reason.Should().Be(@"A file this installer needs is missing: CloudImaging\Drivers\sp2\setup.exe.");
        Encoding.UTF8.GetString(files.Files[@"Windows\Setup\CloudImaging\manifest.json"]).Should().NotContain("Realtek");
        result.WarningCount.Should().Be(1);
    }

    [Fact]
    public void AMissingInstallerFile_UnderFailTheDeployment_StopsStagingAndEverythingAfterIsNotRun()
    {
        var (service, _, _, files) = CreateWithScripts();
        files.Files[@"CloudImaging\Drivers\b.exe"] = [1];
        var content = new SetupProfileContent { FailureHandling = SetupFailureHandling.FailDeployment, Scripts = [Script("Setup", SetupScriptPhase.WindowsSetup, 1)] };

        var result = RunWith(service, content, [Installer("Realtek Audio Console", @"CloudImaging\Drivers\a.exe"), Installer("HP Hotkey Support", @"CloudImaging\Drivers\b.exe")]);

        result.Status.Should().Be(ImagingStepStatus.Failed);
        result.ErrorDetail.Should().Be(@"Realtek Audio Console: A file this installer needs is missing: CloudImaging\Drivers\a.exe.");
        result.Items.Select(i => (i.Name, i.Outcome)).Should().Equal(
            ("Realtek Audio Console", StepItemOutcome.Failed),
            ("HP Hotkey Support", StepItemOutcome.NotRun),
            ("Setup", StepItemOutcome.NotRun),
            ("Answer file", StepItemOutcome.NotRun));
        result.Items.Where(i => i.Outcome == StepItemOutcome.NotRun).Should().OnlyContain(i => i.Reason == "Not run because an earlier item failed and this profile fails the deployment.");
        files.Files.Keys.Should().NotContain(@"Windows\Setup\CloudImaging\manifest.json");
    }

    [Fact]
    public void ARunnerThatCannotBeStaged_FailsEveryQueuedItem_AndTheAnswerFileHasNoRunnerCommand()
    {
        var (service, _, _, files) = CreateWithScripts();
        files.FailWritesUnder = @"Windows\Setup\CloudImaging\Invoke";
        var content = new SetupProfileContent { Scripts = [Script("Setup", SetupScriptPhase.WindowsSetup, 1)] };

        var result = RunWith(service, content);

        result.Items.Single(i => i.Kind == StepItemKind.WindowsSetupScript).Should().Match<ImagingStepItem>(i =>
            i.Outcome == StepItemOutcome.Failed && i.Reason == "The setup runner could not be staged: The disk is full.");
        files.Events.Should().Contain(@"rmdir Windows\Setup\CloudImaging");
        UnattendComposer.CountRunnerCommands(UnattendComposer.Parse(Encoding.UTF8.GetString(files.Files[PrepareSetupService.AnswerFilePath]))).Should().Be(0);
    }

    [Fact]
    public void WindowsSetupWork_OnAnImageThatIsNotGeneralized_FailsTheAnswerFile()
    {
        var (service, _, _, _) = CreateWithScripts(generalized: false);
        var content = new SetupProfileContent { Scripts = [Script("Setup", SetupScriptPhase.WindowsSetup, 1)] };

        var result = RunWith(service, content);

        result.Items.Single(i => i.Kind == StepItemKind.AnswerFile).Reason.Should().Be("The OS image is not generalized, so Windows setup will not run the setup runner. Capture the image after Sysprep /generalize.");
    }

    [Fact]
    public void WithoutAProfile_DriverInstallersAloneAreStaged_WithTheRunnerInTheAnswerFile()
    {
        var (service, _, _, files) = CreateWithScripts();
        files.Files[@"CloudImaging\Drivers\a.exe"] = [1];
        files.Files[@"CloudImaging\Drivers\b.exe"] = [1];

        var result = RunWith(service, null, [Installer("HP Hotkey Support", @"CloudImaging\Drivers\a.exe"), Installer("Realtek Audio Console", @"CloudImaging\Drivers\b.exe")]);

        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.Items.Select(i => (i.Kind, i.Name, i.Outcome)).Should().Equal(
            (StepItemKind.DriverInstaller, "HP Hotkey Support", StepItemOutcome.Queued),
            (StepItemKind.DriverInstaller, "Realtek Audio Console", StepItemOutcome.Queued),
            (StepItemKind.AnswerFile, "Answer file", StepItemOutcome.Succeeded));
        var manifest = Encoding.UTF8.GetString(files.Files[@"Windows\Setup\CloudImaging\manifest.json"]);
        manifest.Should().Contain("\"profile\": null");
        manifest.IndexOf("HP Hotkey Support", StringComparison.Ordinal).Should().BeLessThan(manifest.IndexOf("Realtek Audio Console", StringComparison.Ordinal));
        UnattendComposer.CountRunnerCommands(UnattendComposer.Parse(Encoding.UTF8.GetString(files.Files[PrepareSetupService.AnswerFilePath]))).Should().Be(1);
    }

    [Fact]
    public void WithoutAProfile_AnAnswerFileWriteFailure_NamesTheInstallersThatWillNotRun()
    {
        var (service, _, _, files) = CreateWithScripts();
        files.Files[@"CloudImaging\Drivers\a.exe"] = [1];
        files.Files[@"CloudImaging\Drivers\b.exe"] = [1];
        files.FailWritesUnder = @"Windows\Panther";

        var result = RunWith(service, null, [Installer("HP Hotkey Support", @"CloudImaging\Drivers\a.exe"), Installer("Realtek Audio Console", @"CloudImaging\Drivers\b.exe")]);

        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.Items.Single(i => i.Kind == StepItemKind.AnswerFile).Reason.Should().Be("The answer file could not be written, so these driver installers will not run: HP Hotkey Support, Realtek Audio Console.");
        Encoding.UTF8.GetString(files.Files[@"Windows\Setup\CloudImaging\manifest.json"]).Should().Contain("\"profile\": null");
    }

    [Fact]
    public void TheProfileAnswerFile_IsTheStartDocument_WithTheRunnerBeforeItsOwnCommands()
    {
        var (service, _, _, files) = CreateWithScripts();
        const string profileFile = """
            <unattend xmlns="urn:schemas-microsoft-com:unattend">
              <settings pass="specialize">
                <component name="Microsoft-Windows-Deployment" processorArchitecture="amd64">
                  <RunSynchronous><RunSynchronousCommand><Order>1</Order><Path>own.cmd</Path></RunSynchronousCommand></RunSynchronous>
                </component>
                <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="amd64"><TimeZone>W. Europe Standard Time</TimeZone></component>
              </settings>
            </unattend>
            """;
        var content = new SetupProfileContent { AnswerFile = new SetupAnswerFile { ContentSha256 = "a1" }, Scripts = [Script("Setup", SetupScriptPhase.WindowsSetup, 1)] };

        RunWith(service, content, answerFile: profileFile);

        var written = UnattendComposer.Parse(Encoding.UTF8.GetString(files.Files[PrepareSetupService.AnswerFilePath]));
        var u = UnattendComposer.Unattend;
        written.Descendants(u + "TimeZone").Single().Value.Should().Be("W. Europe Standard Time");
        written.Descendants(u + "RunSynchronousCommand").Select(c => ((string?)c.Element(u + "Order"), (string?)c.Element(u + "Path")))
            .Should().Equal(("1", UnattendComposer.RunnerCommand), ("2", "own.cmd"));
    }

    [Fact]
    public void WithoutWindowsSetupWork_TheProfileAnswerFileIsWrittenUnchanged()
    {
        var (service, _, _, files) = CreateWithScripts();
        const string profileFile = "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"><settings pass=\"oobeSystem\"><component name=\"Microsoft-Windows-International-Core\"><InputLocale>sv-SE</InputLocale></component></settings></unattend>";

        RunWith(service, new SetupProfileContent { AnswerFile = new SetupAnswerFile { ContentSha256 = "a1" } }, answerFile: profileFile);

        var written = UnattendComposer.Parse(Encoding.UTF8.GetString(files.Files[PrepareSetupService.AnswerFilePath]));
        var u = UnattendComposer.Unattend;
        written.Descendants(u + "InputLocale").Single().Value.Should().Be("sv-SE");
        written.Descendants().Select(e => e.Name.LocalName).Should().Equal("unattend", "settings", "component", "InputLocale");
        UnattendComposer.CountRunnerCommands(written).Should().Be(0);
    }

    [Fact]
    public void StatusLines_CoverEveryPhase()
    {
        var (service, _, _, _) = CreateWithScripts();
        var progress = new List<(string, int)>();
        var content = new SetupProfileContent
        {
            RegistryValues = [Value("A", SetupRegistryHive.Software)],
            Scripts = [Script("One", SetupScriptPhase.WinPE, 1), Script("Two", SetupScriptPhase.WinPE, 2), Script("Three", SetupScriptPhase.WinPE, 3), Script("Setup", SetupScriptPhase.WindowsSetup, 1)],
        };

        RunWith(service, content, progress: progress);

        progress.Select(p => p.Item1).Distinct().Should().Equal("Writing registry values", "Running Offline scripts (1 of 3)", "Running Offline scripts (2 of 3)", "Running Offline scripts (3 of 3)", "Staging Specialize work", "Writing the answer file");
        progress.Select(p => p.Item2).Should().BeInAscendingOrder();
    }

    [Fact]
    public void EachScript_RunsFromItsOwnDownloadedFile()
    {
        var (service, scripts, _, files) = CreateWithScripts();
        var content = new SetupProfileContent { Scripts = [Script("One", SetupScriptPhase.WinPE, 1, "Write-Output 1"), Script("Two", SetupScriptPhase.WinPE, 2, "Write-Output 2")] };
        var texts = content.Scripts.Select(s => SetupProfileContentLoader.TextPath(s.ContentSha256)).ToList();

        RunWith(service, content);

        scripts.TextPaths.Should().Equal(texts);
        files.Events.Should().Contain($@"rmdir {WinPeScriptRunner.WorkFolder}");
        files.Files.Keys.Should().NotContain(k => k.StartsWith(WinPeScriptRunner.WorkFolder, StringComparison.OrdinalIgnoreCase), "the downloaded texts are removed once the scripts are done");
    }

    [Fact]
    public void AScriptWhoseTextWasNotDownloaded_Fails_AndIsNotStarted()
    {
        var (service, scripts, _, _) = CreateWithScripts();
        var content = new SetupProfileContent
        {
            Scripts =
            [
                new SetupScript { Name = "Missing", Runs = SetupScriptPhase.WinPE, Order = 1, ContentSha256 = "0" },
                new SetupScript { Name = "Missing setup", Runs = SetupScriptPhase.WindowsSetup, Order = 1, ContentSha256 = "0" },
            ],
        };

        var result = RunWith(service, content);

        scripts.Runs.Should().BeEmpty();
        result.Items.Where(i => i.Name.StartsWith("Missing", StringComparison.Ordinal)).Should().OnlyContain(i => i.Outcome == StepItemOutcome.Failed && i.Reason == "The script could not be downloaded. Check the device's network connection.");
    }

    [Fact]
    public void SpecializeScripts_AreCopiedFromTheirDownloadedFiles_AndTheWorkFolderIsRemoved()
    {
        var (service, scripts, _, files) = CreateWithScripts();

        RunWith(service, new SetupProfileContent { Scripts = [Script("Setup", SetupScriptPhase.WindowsSetup, 1, "Write-Output 'setup'")] });

        scripts.Runs.Should().BeEmpty();
        files.Files[@"Windows\Setup\CloudImaging\Scripts\0001.ps1"].Should().Equal([.. Bom, .. Encoding.UTF8.GetBytes("Write-Output 'setup'")]);
        files.Events.Should().Contain($@"rmdir {WinPeScriptRunner.WorkFolder}");
        files.Files.Keys.Should().NotContain(k => k.StartsWith(WinPeScriptRunner.WorkFolder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnUnexpectedError_StillRemovesTheDownloadedTexts()
    {
        var (service, scripts, _, files) = CreateWithScripts();
        scripts.Respond = _ => throw new InvalidOperationException("The process could not be started.");
        var content = new SetupProfileContent { Scripts = [Script("Boom", SetupScriptPhase.WinPE, 1, "Write-Output boom"), Script("Setup", SetupScriptPhase.WindowsSetup, 1)] };

        var run = () => RunWith(service, content);

        run.Should().Throw<InvalidOperationException>();
        files.Files.Keys.Should().NotContain(k => k.StartsWith(WinPeScriptRunner.WorkFolder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WithoutScripts_TheWorkFolderIsLeftAlone()
    {
        var (service, _, _, files) = CreateWithScripts();

        RunWith(service, new SetupProfileContent { RegistryValues = [Value("A", SetupRegistryHive.Software)] });

        files.Events.Should().NotContain($@"rmdir {WinPeScriptRunner.WorkFolder}");
    }
}
