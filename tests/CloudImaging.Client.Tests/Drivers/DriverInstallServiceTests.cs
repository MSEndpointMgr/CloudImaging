using System.IO;
using System.Net;
using System.Net.Http;
using CloudImaging.Client.Services;
using CloudImaging.Client.Services.Drivers;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Client.Tests.TestDoubles;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.Contracts.Models.SetupProfiles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests.Drivers;

public sealed class DriverInstallServiceTests : IDisposable
{
    private readonly DriverStepFixture _f = new();

    public void Dispose() => _f.Dispose();

    private Task<DriverInstallResult> RunAsync(SessionDriverSetDescriptor set, List<DriverInstallProgress>? progress = null, Func<string, long>? freeSpace = null) =>
        _f.Service(freeSpace).RunAsync(DriverStepFixture.SessionId, set, _f.Root, "HP", update => progress?.Add(update));

    [Fact]
    public async Task OfflineComponent_IsExtractedWithHpSwitches_InjectedWithDism_AndItsFolderDeleted()
    {
        _f.ProcessesProduce();

        var result = await RunAsync(DriverStepFixture.Set(_f.Component(0, "sp200")));

        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.WarningCount.Should().Be(0);
        var item = DriverStepFixture.Item(result, "sp200");
        item.Should().BeEquivalentTo(new ImagingStepItem
        {
            Index = 0,
            Kind = StepItemKind.DriverComponent,
            Name = "Driver sp200",
            Target = "sp200",
            Outcome = StepItemOutcome.Succeeded,
            DriverAction = DriverAction.Offline,
            ComponentVersion = "1.0",
            DriverOrigin = DriverComponentOrigin.Vendor,
            FromUsbCache = false,
        });
        var folder = Path.Combine(_f.Root, @"CloudImaging\Drivers\000-sp200");
        var (extract, dism) = (_f.Processes.Runs[0].Request, _f.Processes.Runs[1].Request);
        extract.FileName.Should().Be(Path.Combine(folder, "sp200.exe"));
        extract.Arguments.Should().Be($"/s /e /f \"{Path.Combine(folder, "x")}\"");
        dism.FileName.Should().EndWith("dism.exe");
        dism.Arguments.Should().Be($"/Image:{_f.Root} /Add-Driver /Driver:\"{Path.Combine(folder, "x")}\" /Recurse");
        Directory.Exists(folder).Should().BeFalse("the folder is deleted after injection");
    }

    [Fact]
    public async Task HpExtraction_FallsBackToTheLegacySwitches_WhenNothingWasProduced()
    {
        _f.ProcessesProduce(onlyLegacySwitches: true);

        var result = await RunAsync(DriverStepFixture.Set(_f.Component(0, "sp200")));

        DriverStepFixture.Item(result, "sp200").Outcome.Should().Be(StepItemOutcome.Succeeded);
        var folder = Path.Combine(_f.Root, @"CloudImaging\Drivers\000-sp200");
        _f.Processes.Runs.Select(r => r.Request.Arguments).Take(2).Should().Equal($"/s /e /f \"{folder}\\x\"", $"-pdf -e -s -f\"{folder}\\x\"");
    }

    [Fact]
    public async Task OfflineComponent_WithoutAnInf_FailsAsNotExtracted_AndDismNeverRuns()
    {
        _f.ProcessesProduce(inf: false);

        var result = await RunAsync(DriverStepFixture.Set(_f.Component(0, "sp200")));

        DriverStepFixture.Item(result, "sp200").Should().Match<ImagingStepItem>(i => i.Outcome == StepItemOutcome.Failed && i.Reason == "The package could not be extracted.");
        _f.Processes.Runs.Should().NotContain(r => r.Request.FileName.EndsWith("dism.exe", StringComparison.OrdinalIgnoreCase));
        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.WarningCount.Should().Be(1);
    }

    [Fact]
    public async Task DismFailure_IsReportedWithItsExitCode()
    {
        _f.ProcessesProduce(dismExitCode: 2);

        var result = await RunAsync(DriverStepFixture.Set(_f.Component(0, "sp200")));

        DriverStepFixture.Item(result, "sp200").Reason.Should().Be("Windows could not add the drivers (DISM exit code 2).");
    }

    [Fact]
    public async Task TooLittleSpace_FailsBeforeExtracting()
    {
        _f.ProcessesProduce();

        var result = await RunAsync(DriverStepFixture.Set(_f.Component(0, "sp200")), freeSpace: _ => 10);

        DriverStepFixture.Item(result, "sp200").Reason.Should().Be("The Windows volume does not have enough free space to extract this package.");
        _f.Processes.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadFailure_IsAFailedItem_AndOtherComponentsContinue()
    {
        _f.ProcessesProduce();
        var broken = _f.Component(0, "sp200") with { Sha256 = new string('0', 64) };

        var result = await RunAsync(DriverStepFixture.Set(broken, _f.Component(1, "sp201")));

        DriverStepFixture.Item(result, "sp200").Reason.Should().Be("The downloaded file does not match its checksum.");
        DriverStepFixture.Item(result, "sp201").Outcome.Should().Be(StepItemOutcome.Succeeded);
        result.Status.Should().Be(ImagingStepStatus.Completed);
        result.WarningCount.Should().Be(1);
    }

    [Fact]
    public async Task CriticalFailure_FailsTheStep_NamingWhatFailedTheCauseAndTheFix()
    {
        _f.ProcessesProduce();
        var broken = _f.Component(0, "sp200", critical: true) with { Sha256 = new string('0', 64) };

        var result = await RunAsync(DriverStepFixture.Set(broken, _f.Component(1, "sp201")));

        result.Status.Should().Be(ImagingStepStatus.Failed);
        result.WarningCount.Should().Be(1);
        result.ErrorDetail.Should().Be("Critical drivers could not be installed: Driver sp200. The downloaded file does not match its checksum. Check the network connection and the model's driver set in the portal, then retry.");
        result.ErrorDetail!.Length.Should().BeLessThanOrEqualTo(500);
    }

    [Theory]
    [InlineData(DriverSkipReason.NoMatchingModel, "8D41", "HP", 1, "No onboarded model matches this device (system ID 8D41).")]
    [InlineData(DriverSkipReason.NoMatchingModel, "0CB4", "Dell Inc.", 1, "No onboarded model matches this device (system SKU 0CB4).")]
    [InlineData(DriverSkipReason.NoMatchingModel, null, "HP", 1, "No onboarded model matches this device.")]
    [InlineData(DriverSkipReason.NoWindowsRelease, "8D41", "HP", 1, "The OS image has no Windows release set.")]
    [InlineData(DriverSkipReason.ReleaseNotOnboarded, "8D41", "HP", 1, "This model is not onboarded for the image's Windows release.")]
    [InlineData(DriverSkipReason.NoDriverSet, "8D41", "HP", 1, "This model has no driver set yet. Run a sync.")]
    [InlineData(DriverSkipReason.ArchitectureMismatch, "8D41", "HP", 1, "The OS image's architecture does not match this model.")]
    [InlineData(DriverSkipReason.NoneChosen, "8D41", "HP", 0, "No drivers were chosen for this device when imaging started.")]
    public async Task SkipReason_CompletesWithTheNote_AndNoItems(DriverSkipReason reason, string? matchKey, string manufacturer, int warnings, string note)
    {
        var result = await _f.Service().RunAsync(DriverStepFixture.SessionId, new SessionDriverSetDescriptor { SkipReason = reason, MatchKey = matchKey }, _f.Root, manufacturer, null);

        result.Should().BeEquivalentTo(new DriverInstallResult(ImagingStepStatus.Completed, warnings, [], note, null, []));
    }

    [Fact]
    public async Task StatusLines_CountComponents()
    {
        _f.ProcessesProduce();
        var progress = new List<DriverInstallProgress>();

        await RunAsync(DriverStepFixture.Set(_f.Component(0, "sp200"), _f.Component(1, "sp201")), progress);

        progress.Select(p => p.Status).Should().Equal("Downloading drivers (1 of 2)", "Adding drivers (1 of 2)", "Downloading drivers (2 of 2)", "Adding drivers (2 of 2)");
    }

    [Fact]
    public async Task NothingIsWrittenOutsideTheWindowsVolume_AndExtractionTempStaysInTheComponentFolder()
    {
        _f.ProcessesProduce();

        await RunAsync(DriverStepFixture.Set(_f.Component(0, "sp200"), _f.Component(1, "sp201")));

        foreach (var (request, _) in _f.Processes.Runs)
        {
            request.WorkingDirectory.Should().StartWith(_f.Root);
            request.Arguments.Should().NotContain("X:\\").And.NotContain("/Mount-Image").And.NotContain("/ImageFile").And.NotContain("/ForceUnsigned");
            foreach (var value in request.Environment.Values)
            {
                value.Should().StartWith(Path.Combine(_f.Root, @"CloudImaging\Drivers\"));
            }
        }

        Directory.Exists(Path.Combine(_f.Root, "CloudImaging", "Drivers")).Should().BeFalse("every folder is removed and the empty staging folder too");
    }

    [Fact]
    public async Task OfflineAndSpecialize_InjectsAndHandsOffAnInstaller_ForEveryStagedFile()
    {
        _f.ProcessesProduce(alsoWrite: "setup.exe");
        var component = _f.Component(0, "sp200", DriverAction.OfflineAndSpecialize) with { InstallCommand = new DriverInstallCommand { Executable = "setup.exe", Arguments = "/silent" } };

        var result = await RunAsync(DriverStepFixture.Set(component));

        var item = DriverStepFixture.Item(result, "sp200");
        item.Outcome.Should().Be(StepItemOutcome.Succeeded);
        item.DriverAction.Should().Be(DriverAction.OfflineAndSpecialize);
        _f.Processes.Runs.Should().Contain(r => r.Request.FileName.EndsWith("dism.exe", StringComparison.OrdinalIgnoreCase));
        var relative = @"CloudImaging\Drivers\000-sp200";
        var x = Path.Combine(_f.Root, relative, "x");
        var installer = result.Installers.Should().ContainSingle().Subject;
        installer.Name.Should().Be("Driver sp200");
        installer.Files.Should().Equal(
            new SpecializeInstallerFile($@"{relative}\x\netwtw.inf", Sha256(Path.Combine(x, "netwtw.inf"))),
            new SpecializeInstallerFile($@"{relative}\x\setup.exe", Sha256(Path.Combine(x, "setup.exe"))));
        installer.Command.Should().Be(new SpecializeInstallerCommand($@"{relative}\x\setup.exe", "/silent", $@"{relative}\x"));
        installer.TimeoutSeconds.Should().Be(1200);
        installer.SuccessExitCodes.Should().Equal(0, 3010);
        installer.CleanupPaths.Should().Equal(relative);
        Directory.Exists(x).Should().BeTrue("a handed-off folder stays for the setup runner");
    }

    [Fact]
    public async Task Specialize_IsStagedWithoutDism_AndReportedQueued()
    {
        _f.ProcessesProduce(inf: false, alsoWrite: "install.cmd");
        var component = _f.Component(0, "sp200", DriverAction.Specialize) with { InstallCommand = new DriverInstallCommand { Executable = "install.cmd" } };

        var result = await RunAsync(DriverStepFixture.Set(component));

        DriverStepFixture.Item(result, "sp200").Outcome.Should().Be(StepItemOutcome.Queued);
        _f.Processes.Runs.Should().NotContain(r => r.Request.FileName.EndsWith("dism.exe", StringComparison.OrdinalIgnoreCase));
        result.Installers.Should().ContainSingle().Which.Command.Should().Be(new SpecializeInstallerCommand(@"CloudImaging\Drivers\000-sp200\x\install.cmd", string.Empty, @"CloudImaging\Drivers\000-sp200\x"));
        result.WarningCount.Should().Be(0);
    }

    // A custom component ZIP with the given entries, served like any component file.
    private SessionDriverComponent CustomZip(int index, string id, params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        var bytes = buffer.ToArray();
        var url = $"https://storage.test/drivers/custom/{id}/1.0/{id}.zip?sig=x";
        _f.Files[url] = bytes;
        return _f.Component(index, id) with
        {
            Origin = DriverComponentOrigin.Custom,
            Extract = DriverExtractKind.Zip,
            FileName = $"{id}.zip",
            DownloadUrl = url,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
        };
    }

    [Fact]
    public async Task CustomZip_IsUnpackedInProcess_AndInjectedWithoutForceUnsigned_AsCustom()
    {
        _f.ProcessesProduce();

        var result = await RunAsync(DriverStepFixture.Set(CustomZip(0, "custom-1", ("drivers/netwtw.inf", "[Version]"), ("drivers/netwtw.sys", "binary"))));

        var item = DriverStepFixture.Item(result, "custom-1");
        item.Outcome.Should().Be(StepItemOutcome.Succeeded);
        item.DriverOrigin.Should().Be(DriverComponentOrigin.Custom);
        _f.Processes.Runs.Should().ContainSingle("only DISM runs; the ZIP is unpacked in process");
        _f.Processes.Runs[0].Request.Arguments.Should().Contain("/Add-Driver").And.NotContain("/ForceUnsigned");
    }

    [Fact]
    public async Task CustomZip_WithAnEntryOutsideTheFolder_FailsTheComponent_AndWritesNothingOutsideIt()
    {
        _f.ProcessesProduce();

        var result = await RunAsync(DriverStepFixture.Set(CustomZip(0, "custom-1", ("drivers/netwtw.inf", "[Version]"), ("../../../escaped.inf", "[Version]"))));

        DriverStepFixture.Item(result, "custom-1").Reason.Should().Be("The package could not be extracted.");
        Directory.EnumerateFiles(_f.Root, "escaped.inf", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(Path.Combine(Path.GetDirectoryName(_f.Root.TrimEnd(Path.DirectorySeparatorChar))!, "escaped.inf")).Should().BeFalse();
        _f.Processes.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task CustomInstaller_IsStagedWithItsCommand_ForSpecialize()
    {
        _f.ProcessesProduce();
        var component = _f.Component(0, "custom-2", DriverAction.Specialize, extract: DriverExtractKind.None) with
        {
            Origin = DriverComponentOrigin.Custom,
            FileName = "custom-2.exe",
            InstallCommand = new DriverInstallCommand { Executable = "custom-2.exe", Arguments = "/quiet /norestart" },
        };

        var result = await RunAsync(DriverStepFixture.Set(component));

        var folder = @"CloudImaging\Drivers\000-custom-2";
        result.Installers.Should().ContainSingle().Which.Command.Should().Be(new SpecializeInstallerCommand($@"{folder}\custom-2.exe", "/quiet /norestart", folder));
        DriverStepFixture.Item(result, "custom-2").Should().Match<ImagingStepItem>(i => i.Outcome == StepItemOutcome.Queued && i.DriverOrigin == DriverComponentOrigin.Custom);
    }

    [Fact]
    public async Task HpArm64Component_IsStagedUnextracted_AndRunsTheSoftPaqWithS()
    {
        _f.ProcessesProduce();
        var component = _f.Component(0, "sp200", DriverAction.Specialize, extract: DriverExtractKind.None) with { InstallCommand = new DriverInstallCommand { Executable = "sp200.exe", Arguments = "/s" } };

        var result = await RunAsync(DriverStepFixture.Set(component));

        _f.Processes.Runs.Should().BeEmpty("nothing is extracted or injected");
        var folder = @"CloudImaging\Drivers\000-sp200";
        var installer = result.Installers.Should().ContainSingle().Subject;
        installer.Command.Should().Be(new SpecializeInstallerCommand($@"{folder}\sp200.exe", "/s", folder));
        installer.Files.Should().Equal(new SpecializeInstallerFile($@"{folder}\sp200.exe", component.Sha256));
        DriverStepFixture.Item(result, "sp200").Outcome.Should().Be(StepItemOutcome.Queued);
    }

    [Fact]
    public async Task WithoutSpecializeWork_NoInstallerIsReturned_AndEveryFolderIsDeleted()
    {
        _f.ProcessesProduce();

        var result = await RunAsync(DriverStepFixture.Set(_f.Component(0, "sp200"), _f.Component(1, "sp201")));

        result.Installers.Should().BeEmpty();
        Directory.Exists(Path.Combine(_f.Root, "CloudImaging", "Drivers")).Should().BeFalse();
    }

    [Fact]
    public async Task SpecializeWithoutItsExecutable_FailsAsNotExtracted_AndTheFolderIsDeleted()
    {
        _f.ProcessesProduce(inf: false);
        var component = _f.Component(0, "sp200", DriverAction.Specialize) with { InstallCommand = new DriverInstallCommand { Executable = "setup.exe" } };

        var result = await RunAsync(DriverStepFixture.Set(component));

        DriverStepFixture.Item(result, "sp200").Reason.Should().Be("The package could not be extracted.");
        result.Installers.Should().BeEmpty();
        Directory.Exists(Path.Combine(_f.Root, @"CloudImaging\Drivers\000-sp200")).Should().BeFalse();
    }

    [Fact]
    public async Task ACommandThatRunsAnExecutableOutsideThePackage_IsRefused_EvenWhenItExists()
    {
        _f.ProcessesProduce(inf: false);
        Directory.CreateDirectory(Path.Combine(_f.Root, "Windows"));
        File.WriteAllText(Path.Combine(_f.Root, "Windows", "tool.exe"), "MZ");
        var component = _f.Component(0, "sp200", DriverAction.Specialize) with { InstallCommand = new DriverInstallCommand { Executable = @"..\..\..\Windows\tool.exe" } };

        var result = await RunAsync(DriverStepFixture.Set(component));

        DriverStepFixture.Item(result, "sp200").Outcome.Should().Be(StepItemOutcome.Failed);
        result.Installers.Should().BeEmpty();
    }

    [Fact]
    public async Task AZipThatExpandsBeyondItsDeclaredSizes_IsRefused()
    {
        var zipPath = Path.Combine(_f.Root, "big.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            for (var i = 0; i <= ZipPackageExtractor.MaxEntries; i++)
            {
                zip.CreateEntry($"f{i}.inf");
            }
        }

        var act = () => new ZipPackageExtractor().ExtractAsync(zipPath, Path.Combine(_f.Root, "x"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidDataException>();
        Directory.Exists(Path.Combine(_f.Root, "x")).Should().BeFalse();
    }

    [Fact]
    public async Task AComponentForHardwareTheDeviceLacks_IsSkipped_AndNeverDownloaded()
    {
        _f.ProcessesProduce();
        var pci = new FakePciInventory(new PciHardwareId("8086", "43F0", "8086", "0074"));
        var missing = _f.Component(0, "CP0VC", extract: DriverExtractKind.Dell64) with { PciIds = [new DriverPciId { Vendor = "10EC", Device = "8168" }] };
        var present = _f.Component(1, "HVVX9", extract: DriverExtractKind.Dell64) with { PciIds = [new DriverPciId { Vendor = "8086", Device = "43F0", SubVendor = "8086", SubDevice = "0074" }] };
        var anyDevice = _f.Component(2, "J4FVY", extract: DriverExtractKind.Dell64);

        var result = await _f.Service(pci: pci).RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(missing, present, anyDevice), _f.Root, "Dell Inc.", null);

        var skipped = DriverStepFixture.Item(result, "CP0VC");
        skipped.Outcome.Should().Be(StepItemOutcome.Skipped);
        skipped.Reason.Should().Be("No matching hardware in this device.");
        skipped.FromUsbCache.Should().BeNull();
        _f.Storage.Requests.Should().NotContain(r => r.Request.RequestUri!.AbsolutePath.Contains("CP0VC", StringComparison.Ordinal));
        DriverStepFixture.Item(result, "HVVX9").Outcome.Should().Be(StepItemOutcome.Succeeded);
        DriverStepFixture.Item(result, "J4FVY").Outcome.Should().Be(StepItemOutcome.Succeeded, "a component without PCI IDs is processed");
        result.WarningCount.Should().Be(0, "a skipped component is not a failure");
        pci.Reads.Should().Be(1, "the hardware is read once per step");
    }

    private string UsbRoot => Path.Combine(_f.Root, "usb", "CloudImagingCache");

    [Fact]
    public async Task AMiss_DownloadsAndThenCaches_AndTheNextDeviceCopiesFromTheUsbDrive()
    {
        _f.ProcessesProduce();
        var component = _f.Component(0, "sp200");
        var cache = new DriverFileCache(UsbRoot, NullLogger.Instance, _ => long.MaxValue);

        var first = await _f.Service(cache: cache).RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(component), _f.Root, "HP", null);
        var downloads = _f.Storage.Requests.Count;
        var progress = new List<DriverInstallProgress>();
        var second = await _f.Service(cache: cache).RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(component), _f.Root, "HP", progress.Add);

        DriverStepFixture.Item(first, "sp200").FromUsbCache.Should().BeFalse();
        File.Exists(Path.Combine(UsbRoot, "drivers", component.Sha256, "metadata.json")).Should().BeTrue();
        DriverStepFixture.Item(second, "sp200").Should().Match<ImagingStepItem>(i => i.FromUsbCache == true && i.Outcome == StepItemOutcome.Succeeded);
        _f.Storage.Requests.Count.Should().Be(downloads, "a hit downloads nothing");
        progress[0].Status.Should().Be("Copying drivers from the USB drive (1 of 1)");
    }

    [Fact]
    public async Task TheHardwareMatchRunsBeforeTheCacheLookup()
    {
        _f.ProcessesProduce();
        var pci = new FakePciInventory(new PciHardwareId("8086", "43F0", "8086", "0074"));
        var missing = _f.Component(0, "CP0VC", extract: DriverExtractKind.Dell64) with { PciIds = [new DriverPciId { Vendor = "10EC", Device = "8168" }] };
        var cache = new DriverFileCache(UsbRoot, NullLogger.Instance, _ => long.MaxValue);
        await PreloadAsync(missing);

        var result = await _f.Service(pci: pci, cache: cache).RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(missing), _f.Root, "Dell Inc.", null);

        DriverStepFixture.Item(result, "CP0VC").Should().Match<ImagingStepItem>(i => i.Outcome == StepItemOutcome.Skipped && i.FromUsbCache == null);
        Directory.Exists(Path.Combine(_f.Root, @"CloudImaging\Drivers\000-CP0VC")).Should().BeFalse();
    }

    [Fact]
    public async Task AFailedCacheWrite_NeverFailsTheComponent()
    {
        _f.ProcessesProduce();
        File.WriteAllText(Path.Combine(_f.Root, "blocker"), "x");
        var cache = new DriverFileCache(Path.Combine(_f.Root, "blocker"), NullLogger.Instance, _ => long.MaxValue);

        var result = await _f.Service(cache: cache).RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(_f.Component(0, "sp200")), _f.Root, "HP", null);

        DriverStepFixture.Item(result, "sp200").Should().Match<ImagingStepItem>(i => i.Outcome == StepItemOutcome.Succeeded && i.FromUsbCache == false);
    }

    [Fact]
    public async Task EachItem_ReportsWhereItsFileCameFrom()
    {
        _f.ProcessesProduce();
        var cached = _f.Component(0, "sp200");
        var fresh = _f.Component(1, "sp201");
        var cache = new DriverFileCache(UsbRoot, NullLogger.Instance, _ => long.MaxValue);
        await PreloadAsync(cached);

        var result = await _f.Service(cache: cache).RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(cached, fresh), _f.Root, "HP", null);

        DriverStepFixture.Item(result, "sp200").FromUsbCache.Should().BeTrue();
        DriverStepFixture.Item(result, "sp201").FromUsbCache.Should().BeFalse();
    }

    private async Task PreloadAsync(SessionDriverComponent component)
    {
        var source = Path.Combine(_f.Root, "src", component.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, _f.Files[component.DownloadUrl]);
        await new DriverFileCache(UsbRoot, NullLogger.Instance, _ => long.MaxValue).AddAsync(component.Sha256, source);
    }

    [Fact]
    public async Task DellExtraction_RunsTheDriversSwitch()
    {
        _f.ProcessesProduce();

        await _f.Service().RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(_f.Component(0, "HVVX9", extract: DriverExtractKind.Dell64)), _f.Root, "Dell Inc.", null);

        var folder = Path.Combine(_f.Root, @"CloudImaging\Drivers\000-HVVX9");
        _f.Processes.Runs[0].Request.FileName.Should().Be(Path.Combine(folder, "HVVX9.exe"));
        _f.Processes.Runs[0].Request.Arguments.Should().Be($"/s /drivers=\"{Path.Combine(folder, "x")}\"");
    }

    [Fact]
    public async Task ADell32BitSpecializePackage_IsStagedAsItIs_AndRunsWithS()
    {
        _f.ProcessesProduce();
        var component = _f.Component(0, "CP0VC", DriverAction.Specialize, extract: DriverExtractKind.None) with { InstallCommand = new DriverInstallCommand { Executable = "CP0VC.exe", Arguments = "/s" } };

        var result = await _f.Service().RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(component), _f.Root, "Dell Inc.", null);

        _f.Processes.Runs.Should().BeEmpty("a 32-bit package never runs in WinPE");
        var folder = @"CloudImaging\Drivers\000-CP0VC";
        result.Installers.Should().ContainSingle().Which.Command.Should().Be(new SpecializeInstallerCommand($@"{folder}\CP0VC.exe", "/s", folder));
        DriverStepFixture.Item(result, "CP0VC").Outcome.Should().Be(StepItemOutcome.Queued);
    }

    [Fact]
    public async Task ADellDriverAndApplication_InjectsTheDrivers_AndRunsThePackageDuringSpecialize()
    {
        _f.ProcessesProduce();
        var component = _f.Component(0, "VY2CK", DriverAction.OfflineAndSpecialize, extract: DriverExtractKind.Dell64) with { InstallCommand = new DriverInstallCommand { Executable = "VY2CK.exe", Arguments = "/s" } };

        var result = await _f.Service().RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(component), _f.Root, "Dell Inc.", null);

        DriverStepFixture.Item(result, "VY2CK").Outcome.Should().Be(StepItemOutcome.Succeeded);
        _f.Processes.Runs.Should().Contain(r => r.Request.FileName.EndsWith("dism.exe", StringComparison.OrdinalIgnoreCase));
        var folder = @"CloudImaging\Drivers\000-VY2CK";
        var installer = result.Installers.Should().ContainSingle().Subject;
        installer.Command.Should().Be(new SpecializeInstallerCommand($@"{folder}\VY2CK.exe", "/s", folder));
        installer.Files.Select(f => f.Path).Should().Contain($@"{folder}\VY2CK.exe").And.Contain($@"{folder}\x\netwtw.inf");
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task ProgressToTheGateway_IsAggregated_ToAtMostTenCallsPerThirtySeconds()
    {
        _f.ProcessesProduce();
        var components = Enumerable.Range(0, 31).Select(i => _f.Component(i, $"sp{300 + i}")).ToArray();
        var reporter = new ImagingProgressReporter(_f.Client, DriverStepFixture.SessionId, NullLogger<ImagingProgressReporter>.Instance, new FixedTimeProvider(DriverStepFixture.Now));

        await _f.Service().RunAsync(DriverStepFixture.SessionId, DriverStepFixture.Set(components), _f.Root, "HP", update => reporter.ReportAsync(ImagingStepName.InstallDrivers, ImagingStepStatus.InProgress, update.Percent).GetAwaiter().GetResult());

        _f.Gateway.Requests.Count(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/progress", StringComparison.Ordinal)).Should().BeLessThanOrEqualTo(10);
    }
}
