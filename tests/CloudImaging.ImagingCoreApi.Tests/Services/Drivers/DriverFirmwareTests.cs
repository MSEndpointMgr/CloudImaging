using System.Security.Cryptography;
using System.Text;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

public sealed class DriverFirmwareTests
{
    private const string Platform = "8C26";
    private static readonly DriverActor Admin = new() { Upn = "admin@contoso.com" };
    private static readonly Guid Lab = Guid.Parse("5d0c6c2a-3f0e-4c55-9d0a-1f0d6f4a9b01");

    private readonly DriverSyncHarness _h = new();
    private readonly DriverFirmwareService _firmware;

    public DriverFirmwareTests()
    {
        _firmware = new DriverFirmwareService(_h.Bed.Store, _h.Bed.Models, _h.Storage, _h.Bed.Work, new LocationRepository(_h.Bed.Tables), _h.Bed.Time);
    }

    [Fact]
    public void Parse_ListsVerifiableBinFiles_NewestVersionFirst()
    {
        var xml = Catalog(("01.09.00", true), ("01.10.00", true), ("01.08.00", false)) + string.Empty;
        xml = xml.Replace("</BIOS>", "<Rel Ver=\"01.11.00\" Date=\"2026-09-01\" Bin=\"T74_01110000.exe\" Sha256=\"" + new string('a', 64) + "\"/></BIOS>", StringComparison.Ordinal);

        var releases = HpBiosCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), Platform);

        // 01.10 sorts above 01.09 by number; the entry without a SHA-256 and the .exe are left out.
        releases.Select(r => r.Version).Should().Equal("01.10.00", "01.09.00");
        releases[0].FileName.Should().Be("T74_01100000.bin");
        releases[0].Url.Should().Be("https://ftp.hp.com/pub/pcbios/8C26/T74_01100000.bin");
        releases[0].ReleasedAt.Should().Be(new DateOnly(2026, 7, 30));
        releases[0].Critical.Should().BeTrue();
    }

    [Theory]
    [InlineData("X84 Ver. 01.06.01", "01.06.01")]
    [InlineData("T74 Ver. 01.10.00", "01.10.00")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("Not a version", null)]
    [InlineData(null, null)]
    public void InstalledVersion_ReadsTheVersionFromTheSmbiosString(string? smbios, string? expected) =>
        HpBiosCatalog.InstalledVersion(smbios).Should().Be(expected);

    [Fact]
    public async Task Sync_ListsTheBios_SkippedByDefault_AndDownloadsNothing()
    {
        var model = await OnboardAsync();
        Publish(("01.09.00", true), ("01.10.00", true));

        var run = await _h.RunAsync();

        var page = (await _firmware.GetPageAsync(model.ModelId))!;
        page.Supported.Should().BeTrue();
        var row = page.Rows.Should().ContainSingle().Subject;
        row.PlatformId.Should().Be(Platform);
        row.Version.Should().Be("01.10.00");
        row.Action.Should().Be(DriverFirmwareAction.Skip);
        row.Source.Should().Be(DriverActionSource.Default);
        (await _h.Bed.Store.Files.ListAllAsync()).Should().NotContain(f => f.Row.Value.Category == DriverFirmwareService.Category);
        (await _h.Bed.Runs.ListItemsAsync(run.RunId)).Should().NotContain(i => i.Category == DriverFirmwareService.Category, "the first read of a platform is not reported as a change");
    }

    [Fact]
    public async Task UpdateAtRestart_DownloadsTheBios_AndPinsADeviceWithAnOlderOne()
    {
        var model = await OnboardAsync();
        Publish(("01.09.00", true), ("01.10.00", true));
        await _h.RunAsync();

        var result = await _firmware.SetActionAsync(model.ModelId, Platform, new DriverFirmwareActionRequest { Action = DriverFirmwareAction.UpdateAtRestart, Actor = Admin });
        await _h.PumpAsync();

        result.Status.Should().Be(System.Net.HttpStatusCode.OK);
        var file = (await _h.Bed.Store.Files.GetAsync(nameof(DriverVendor.Hp), DriverFirmwareService.FileKey(Platform, "01.10.00")))!.Value;
        file.Status.Should().Be(DriverFileStatus.Verified);
        file.Size.Should().Be(Bin("01.10.00").Length);

        var pin = await _firmware.PinAsync(Payload("T74 Ver. 01.09.00"));
        pin.Should().Be(new DriverFirmwarePin(model.ModelId, Platform, "01.10.00", "01.09.00"));
        (await _firmware.PinAsync(Payload("T74 Ver. 01.10.00"))).Should().BeNull("the device is up to date");
        (await _firmware.PinAsync(Payload("T74 Ver. 01.09.00", architecture: MachineArchitecture.Arm64))).Should().BeNull();

        var descriptor = (await _firmware.DescriptorAsync(pin!.ApplyTo(Session())))!;
        descriptor.FileName.Should().Be("T74_01100000.bin");
        descriptor.Sha256.Should().Be(Sha(Bin("01.10.00")));
        descriptor.InstalledVersion.Should().Be("01.09.00");
    }

    [Fact]
    public async Task History_TheCurrentVersionSaysWhetherItsFileIsThereYet()
    {
        var model = await OnboardAsync();
        Publish(("01.09.00", true), ("01.10.00", true));
        await _h.RunAsync();

        (await CurrentStatusAsync(model)).Should().Be(DriverVersionStatus.NotDownloaded, "a skipped BIOS is not downloaded, and the current version is not something to roll back to");

        await _firmware.SetActionAsync(model.ModelId, Platform, new DriverFirmwareActionRequest { Action = DriverFirmwareAction.UpdateAtRestart, Actor = Admin });
        (await CurrentStatusAsync(model)).Should().Be(DriverVersionStatus.Downloading);

        await _h.PumpAsync();
        (await CurrentStatusAsync(model)).Should().Be(DriverVersionStatus.Stored);
    }

    [Fact]
    public async Task History_ACurrentVersionThatCouldNotBeDownloaded_SaysSo()
    {
        var model = await OnboardAsync();
        Publish(("01.10.00", true));
        await _h.RunAsync();
        _h.Bed.Vendor.ServeStatus(HpBiosCatalog.FileUrl(Platform, FileName("01.10.00")), System.Net.HttpStatusCode.NotFound);

        await _firmware.SetActionAsync(model.ModelId, Platform, new DriverFirmwareActionRequest { Action = DriverFirmwareAction.UpdateAtRestart, Actor = Admin });
        await _h.PumpAsync();

        (await CurrentStatusAsync(model)).Should().Be(DriverVersionStatus.Failed);
    }

    [Fact]
    public async Task PreProductionFirst_GivesANewerBiosOnlyToThePreProductionLocation_UntilReleased()
    {
        var model = await OnboardAsync();
        Publish(("01.09.00", true));
        await _h.RunAsync();
        await _firmware.SetActionAsync(model.ModelId, Platform, new DriverFirmwareActionRequest { Action = DriverFirmwareAction.UpdateAtRestart, Actor = Admin });
        await _h.Bed.Store.SyncSettings.UpsertAsync(DriverStore.SettingsPartition, DriverStore.SettingsRow, new DriverSyncSettingsRecord { FirmwareNewVersions = DriverFirmwareNewVersions.PreProductionFirst, FirmwarePreProductionLocationId = Lab });
        Publish(("01.09.00", true), ("01.10.00", true));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        await _h.RunAsync();

        var page = (await _firmware.GetPageAsync(model.ModelId))!;
        page.PreProduction.Should().ContainSingle(p => p.Version == "01.10.00" && p.ReleasedVersion == "01.09.00");
        (await _firmware.PinAsync(Payload("T74 Ver. 01.08.00", Lab)))!.Version.Should().Be("01.10.00");
        (await _firmware.PinAsync(Payload("T74 Ver. 01.08.00", Guid.NewGuid())))!.Version.Should().Be("01.09.00");

        await _firmware.ReleaseAsync(model.ModelId, Platform, new DriverActorRequest { Actor = Admin });

        (await _firmware.PinAsync(Payload("T74 Ver. 01.08.00", Guid.NewGuid())))!.Version.Should().Be("01.10.00");
        var history = (List<DriverComponentVersion>)(await _firmware.VersionsAsync(model.ModelId, Platform)).Body!;
        history[0].Should().Match<DriverComponentVersion>(v => v.Version == "01.10.00" && v.Change == DriverVersionChange.Release && v.ChangedBy == Admin.Upn && v.To == null);
        history[1].To.Should().NotBeNull();
    }

    [Fact]
    public async Task RollBack_HoldsAnEarlierBios_UntilHpPublishesANewerOne()
    {
        var model = await OnboardAsync();
        Publish(("01.09.00", true), ("01.10.00", true));
        await _h.RunAsync();
        await _firmware.SetActionAsync(model.ModelId, Platform, new DriverFirmwareActionRequest { Action = DriverFirmwareAction.UpdateAtRestart, Actor = Admin });

        var rolledBack = await _firmware.RollBackAsync(model.ModelId, Platform, new DriverComponentRollBackRequest { Version = "01.09.00", Actor = Admin });
        await _h.PumpAsync();

        rolledBack.Status.Should().Be(System.Net.HttpStatusCode.OK);
        (await _firmware.PinAsync(Payload("T74 Ver. 01.08.00")))!.Version.Should().Be("01.09.00");
        ((DriverFirmwarePage)rolledBack.Body!).Rows[0].Source.Should().Be(DriverActionSource.RolledBack);

        Publish(("01.09.00", true), ("01.10.00", true), ("01.11.00", true));
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        await _h.RunAsync();

        var record = (await _h.Bed.Store.Firmware.GetAsync(model.ModelId.ToString("D"), Platform))!.Value;
        record.RollBack.Should().BeNull();
        record.ReleasedVersion.Should().Be("01.11.00");
    }

    [Fact]
    public async Task CleanUp_KeepsTheBiosDevicesGet()
    {
        var model = await OnboardAsync();
        Publish(("01.10.00", true));
        await _h.RunAsync();
        await _firmware.SetActionAsync(model.ModelId, Platform, new DriverFirmwareActionRequest { Action = DriverFirmwareAction.UpdateAtRestart, Actor = Admin });
        await _h.PumpAsync();

        await _h.Sync.CleanUpAsync();

        (await _h.Bed.Store.Files.GetAsync(nameof(DriverVendor.Hp), DriverFirmwareService.FileKey(Platform, "01.10.00"))).Should().NotBeNull();
    }

    [Fact]
    public async Task RemovingTheModel_RemovesItsBios()
    {
        var model = await OnboardAsync();
        Publish(("01.10.00", true));
        await _h.RunAsync();

        await _h.Lifecycle.RemoveModelAsync(model.ModelId, Admin.Upn);

        (await _h.Bed.Store.Firmware.ListPartitionAsync(model.ModelId.ToString("D"))).Should().BeEmpty();
        (await _h.Bed.Store.FirmwareVersions.ListAllAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(PowerSource.Unknown, true)]
    [InlineData(PowerSource.Ac, true)]
    public async Task OnlyAClientThatReportsItsPowerSource_GetsABios(PowerSource? power, bool pinned)
    {
        // A Client older than BIOS updates reports no power source and could not stage one.
        await UpdatingModelAsync();

        var pin = await _firmware.PinAsync(Payload("T74 Ver. 01.09.00", power: power));

        (pin is not null).Should().Be(pinned);
    }

    [Fact]
    public async Task RemovingTheModel_KeepsTheBiosOfARunningDeployment()
    {
        var model = await UpdatingModelAsync();
        var pin = (await _firmware.PinAsync(Payload("T74 Ver. 01.09.00")))!;
        var session = pin.ApplyTo(Session());
        await _h.Sessions.CreateAsync(session);

        await _h.Lifecycle.RemoveModelAsync(model.ModelId, Admin.Upn);
        await _h.Sync.CleanUpAsync();

        var response = await GetSessionFirmwareAsync(session.SessionId);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.ReadJsonAsync();
        body.GetProperty("version").GetString().Should().Be("01.10.00");
        body.GetProperty("fileName").GetString().Should().Be("T74_01100000.bin");
        body.GetProperty("sha256").GetString().Should().Be(Sha(Bin("01.10.00")));
    }

    [Fact]
    public async Task SessionFirmware_Is204WithoutABios_And410WhenItsFileIsGone()
    {
        await UpdatingModelAsync();
        var none = Session();
        var gone = new DriverFirmwarePin(Guid.NewGuid(), Platform, "01.07.00", null).ApplyTo(Session());
        await _h.Sessions.CreateAsync(none);
        await _h.Sessions.CreateAsync(gone);

        (await GetSessionFirmwareAsync(none.SessionId)).StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
        (await GetSessionFirmwareAsync(gone.SessionId)).StatusCode.Should().Be(System.Net.HttpStatusCode.Gone, "the device reports the BIOS as not updated instead of hiding the step");
    }

    private async Task<DriverModelRecord> UpdatingModelAsync()
    {
        var model = await OnboardAsync();
        Publish(("01.10.00", true));
        await _h.RunAsync();
        await _firmware.SetActionAsync(model.ModelId, Platform, new DriverFirmwareActionRequest { Action = DriverFirmwareAction.UpdateAtRestart, Actor = Admin });
        await _h.PumpAsync();
        return model;
    }

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> GetSessionFirmwareAsync(Guid sessionId) =>
        new CloudImaging.ImagingCoreApi.Functions.DriverFirmwareFunctions(_firmware, _h.Sessions)
            .GetSessionFirmware(FunctionTestHost.Request("GET", $"https://core/api/internal/sessions/{sessionId}/firmware"), sessionId.ToString(), FunctionTestHost.Context);

    private async Task<DriverModelRecord> OnboardAsync()
    {
        await _h.EnableAsync();
        return await _h.OnboardAsync(Platform, "HP EliteBook 840 G11");
    }

    private async Task<DriverVersionStatus> CurrentStatusAsync(DriverModelRecord model) =>
        ((List<DriverComponentVersion>)(await _firmware.VersionsAsync(model.ModelId, Platform)).Body!).Single(v => v.To is null).Status;

    private void Publish(params (string Version, bool Critical)[] releases)
    {
        _h.Bed.Vendor.ServeBytes(HpBiosCatalog.CatalogUrl(Platform), Encoding.UTF8.GetBytes(Catalog(releases)));
        foreach (var (version, _) in releases)
        {
            _h.Bed.Vendor.ServeBytes(HpBiosCatalog.FileUrl(Platform, FileName(version)), Bin(version));
        }
    }

    private static string Catalog(params (string Version, bool Critical)[] releases)
    {
        var entries = releases.Select(r => r.Version == "01.08.00"
            ? $"<Rel Ver=\"{r.Version}\" Date=\"2026-01-10\" Bin=\"{FileName(r.Version)}\" RB=\"1\" L=\"0\"/>"
            : $"<Rel Ver=\"{r.Version}\" Date=\"2026-07-30\" Bin=\"{FileName(r.Version)}\" RB=\"1\" L=\"{(r.Critical ? 1 : 0)}\" Sha256=\"{Sha(Bin(r.Version)).ToUpperInvariant()}\"/>");
        return $"<?xml version=\"1.0\" encoding=\"utf-8\"?><BIOS>{string.Concat(entries)}</BIOS>";
    }

    private static string FileName(string version) => $"T74_{version.Replace(".", string.Empty, StringComparison.Ordinal)}00.bin";

    private static byte[] Bin(string version) => Encoding.UTF8.GetBytes($"bios {version}");

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static DeviceHardwareMetadata Hardware(string biosVersion) => new() { MotherboardManufacturer = "HP", MotherboardModel = Platform.ToLowerInvariant(), BiosVersion = biosVersion };

    private static DeviceRegistrationPayload Payload(string biosVersion, Guid? location = null, MachineArchitecture architecture = MachineArchitecture.X64, PowerSource? power = PowerSource.Ac) => new()
    {
        SerialNumber = "5CG0000000",
        Manufacturer = "HP",
        Model = "HP EliteBook 840 G11",
        Hardware = Hardware(biosVersion),
        LocationId = location,
        Architecture = architecture,
        SecurityPosture = new DeviceSecurityPosture { FirmwareMode = FirmwareMode.Uefi, PowerSource = power },
    };

    private static DeviceSession Session() => new()
    {
        SessionId = Guid.NewGuid(),
        DeviceSerialNumber = "5CG0000000",
        DeviceManufacturer = "HP",
        DeviceModel = "HP EliteBook 840 G11",
        State = SessionState.SessionInProgress,
    };
}
