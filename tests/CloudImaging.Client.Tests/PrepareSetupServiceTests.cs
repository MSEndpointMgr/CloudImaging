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
        return (new PrepareSetupService(hive, files, NullLogger.Instance), hive, files, []);
    }

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
}
