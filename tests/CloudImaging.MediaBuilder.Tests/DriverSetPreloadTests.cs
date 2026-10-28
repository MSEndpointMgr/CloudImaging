using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.MediaBuilder.Services;
using CloudImaging.MediaBuilder.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>Preloading driver sets on a USB drive: choosing, sizing, downloading and copying.</summary>
public sealed class DriverSetPreloadTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ci-mb-drivers-" + Guid.NewGuid().ToString("N"));

    public DriverSetPreloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static MediaBuilderDriverSet Set(string vendor, string model, params (string Sha, long Size)[] files) => new()
    {
        ModelId = Guid.NewGuid(),
        Vendor = vendor == "Dell" ? DriverVendor.Dell : DriverVendor.Hp,
        VendorName = vendor,
        ModelName = model,
        Release = WindowsRelease.Windows11Version24H2,
        ReleaseName = "Windows 11, version 24H2",
        SetVersion = 3,
        Components = files.Length,
        Bytes = files.Sum(f => f.Size),
        Files = [.. files.Select(f => new MediaBuilderDriverSetFile(f.Sha, f.Size))],
    };

    // Section

    [Fact]
    public void Sets_AreGroupedByTheVendorNamesFromTheResponse_AndNothingIsSelected()
    {
        var selection = new DriverSetSelection();

        selection.Load(MachineArchitecture.X64, [Set("HP", "HP EliteBook 840 G11", ("a", 1)), Set("Dell", "Latitude 7450", ("b", 1)), Set("HP", "HP ZBook", ("c", 1))]);

        selection.Groups.Select(g => g.VendorName).Should().Equal("HP", "Dell");
        selection.Groups[0].Rows.Select(r => r.ModelName).Should().Equal("HP EliteBook 840 G11", "HP ZBook");
        selection.Selected.Should().BeEmpty();
        selection.Summary.Should().BeNull();
    }

    [Fact]
    public void Sizes_CountSharedFilesOnce()
    {
        var selection = new DriverSetSelection { CacheBytes = 30 * Gb };
        selection.Load(MachineArchitecture.X64, [Set("HP", "A", ("shared", 2 * Gb), ("a", Gb)), Set("HP", "B", ("shared", 2 * Gb), ("b", Gb))]);

        foreach (var row in selection.Groups[0].Rows)
        {
            row.IsSelected = true;
        }

        selection.SelectedBytes.Should().Be(4 * Gb);
        selection.Summary.Should().Be("2 driver sets, 4.0 GB. 26.0 GB stays free for OS images.");
        selection.ShowLowSpaceWarning.Should().BeFalse();
    }

    [Fact]
    public void LessThanTwentyGigabytesFree_Warns_AndASelectionThatDoesNotFit_DisablesPrepare()
    {
        var selection = new DriverSetSelection { CacheBytes = 22 * Gb };
        selection.Load(MachineArchitecture.X64, [Set("HP", "A", ("a", 5 * Gb)), Set("HP", "B", ("b", 20 * Gb))]);

        selection.Groups[0].Rows[0].IsSelected = true;
        selection.ShowLowSpaceWarning.Should().BeTrue();
        selection.DoesNotFit.Should().BeFalse();

        selection.Groups[0].Rows[1].IsSelected = true;
        selection.DoesNotFit.Should().BeTrue();
        selection.ShowLowSpaceWarning.Should().BeFalse("the error replaces the warning");
    }

    [Fact]
    public void NoSetsForTheArchitecture_SaysSo()
    {
        var selection = new DriverSetSelection();

        selection.Load(MachineArchitecture.Arm64, []);

        selection.EmptyText.Should().Be("No driver sets are available for ARM64 boot images.");
    }

    [Fact]
    public void TheDoesNotFitError_DisablesPrepare_AndCarriesASupportReferenceAndTheFix()
    {
        var vm = CreateViewModel();
        vm.SelectedBootImage = new PrepareStorageDeviceViewModel.BootImageChoice(Guid.NewGuid(), "v1", new BootImageDto { BootImageId = Guid.NewGuid(), Version = "1", Sha256Hash = "abc", IsActive = true });
        vm.SelectedDisk = new PrepareStorageDeviceViewModel.DiskChoice(1, "Disk 1", new UsbSafetyValidationService.DiskInfo(1, "USB", 16 * Gb, "USB", true, false), true, null);
        vm.ConfirmErase = true;
        vm.CanPrepare.Should().BeTrue();

        vm.DriverSets.Load(MachineArchitecture.X64, [Set("HP", "Huge", ("a", 15 * Gb))]);
        vm.DriverSets.Groups[0].Rows[0].IsSelected = true;

        vm.CanPrepare.Should().BeFalse();
        vm.DriverSetsDoNotFitMessage.Should().StartWith("The selected driver sets do not fit on this USB drive. Clear some of them or use a larger drive.").And.MatchRegex(@"CMB-PREPUSB-DRV-\d+$");
    }

    [Fact]
    public void TheDownloadProgressLine_CountsFilesAndGigabytes()
    {
        PrepareStorageDeviceViewModel.DriverDownloadText(new DriverDownloadProgress(12, 58, (long)(1.1 * Gb), (long)(4.8 * Gb)))
            .Should().Be("Downloading driver sets (12 of 58 files, 1.1 of 4.8 GB)");
    }

    // Download

    private sealed class FileServer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };

    private static MediaBuilderDriverFile File(string content, string name = "sp200.exe") =>
        new() { Sha256 = Sha(content), FileName = name, SizeBytes = content.Length, Url = $"https://storage.test/{name}?sig=x", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60) };

    [Fact]
    public async Task FilesAlreadyInTheLocalCache_AreNotDownloadedAgain()
    {
        var server = new FileServer(_ => Ok("softpaq"));
        var waits = new List<TimeSpan>();
        var service = new DriverSetDownloadService(new HttpClient(server), NullLogger.Instance, _root, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        var first = await service.DownloadAsync([File("softpaq")], null);
        var second = await service.DownloadAsync([File("softpaq")], null);

        server.Requests.Should().HaveCount(1);
        second.Single().SourcePath.Should().Be(first.Single().SourcePath);
        System.IO.File.Exists(Path.Combine(_root, Sha("softpaq"), "metadata.json")).Should().BeTrue();
    }

    [Fact]
    public async Task AFileThatKeepsFailing_StopsWithItsMessage_AfterThreeAttempts()
    {
        var server = new FileServer(_ => Ok("tampered"));
        var waits = new List<TimeSpan>();
        var service = new DriverSetDownloadService(new HttpClient(server), NullLogger.Instance, _root, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        var act = () => service.DownloadAsync([File("softpaq"), File("other", "sp201.exe")], null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("2 driver files could not be downloaded. Try again, or clear the driver sets.");
        server.Requests.Should().HaveCount(6);
        waits.Take(2).Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        Directory.GetDirectories(_root).Should().BeEmpty("nothing unverified is kept");
    }

    [Fact]
    public async Task ADamagedCachedFile_IsDownloadedAgain()
    {
        var server = new FileServer(_ => Ok("softpaq"));
        var service = new DriverSetDownloadService(new HttpClient(server), NullLogger.Instance, _root, (_, _) => Task.CompletedTask);
        var first = await service.DownloadAsync([File("softpaq")], null);
        System.IO.File.WriteAllText(first.Single().SourcePath, "softpaX");

        await service.DownloadAsync([File("softpaq")], null);

        server.Requests.Should().HaveCount(2);
        System.IO.File.ReadAllText(first.Single().SourcePath).Should().Be("softpaq");
    }

    [Fact]
    public async Task AFileLargerThanListed_IsRefused()
    {
        var server = new FileServer(_ => Ok("softpaq-and-much-more"));
        var service = new DriverSetDownloadService(new HttpClient(server), NullLogger.Instance, _root, (_, _) => Task.CompletedTask);

        var act = () => service.DownloadAsync([File("softpaq")], null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("1 driver file could not be downloaded*");
    }

    [Fact]
    public async Task AnExpiringLink_IsRefreshedBeforeTheDownload()
    {
        var server = new FileServer(_ => Ok("softpaq"));
        var service = new DriverSetDownloadService(new HttpClient(server), NullLogger.Instance, _root, (_, _) => Task.CompletedTask);
        var expiring = File("softpaq") with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        var renewed = File("softpaq") with { Url = "https://storage.test/renewed?sig=y" };

        await service.DownloadAsync([expiring], null, _ => Task.FromResult<IReadOnlyList<MediaBuilderDriverFile>>([renewed]));

        server.Requests.Should().Equal("https://storage.test/renewed?sig=y");
    }

    [Fact]
    public async Task DownloadsRunOffTheCallingThread()
    {
        var gate = new ManualResetEventSlim();
        var server = new FileServer(_ => { gate.Wait(TimeSpan.FromSeconds(5)); return Ok("softpaq"); });
        var service = new DriverSetDownloadService(new HttpClient(server), NullLogger.Instance, _root);
        var watch = Stopwatch.StartNew();

        var task = service.DownloadAsync([File("softpaq")], null);

        watch.ElapsedMilliseconds.Should().BeLessThan(100);
        gate.Set();
        (await task).Should().HaveCount(1);
    }

    // Elevated copy

    private PreloadDriverFile Source(string content, string name = "sp200.exe")
    {
        var path = Path.Combine(_root, "local", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return new PreloadDriverFile(Sha(content), name, content.Length, path);
    }

    [Fact]
    public async Task TheWorkerCopiesIntoTheClientLayout_WithMetadataLast()
    {
        var file = Source("softpaq");
        var cache = Path.Combine(_root, "E", "CloudImagingCache");

        await DriverSetPreloadCopier.CopyAsync(cache, [file], null);

        var dir = Path.Combine(cache, "drivers", file.Sha256);
        System.IO.File.ReadAllText(Path.Combine(dir, "sp200.exe")).Should().Be("softpaq");
        var metadata = JsonDocument.Parse(System.IO.File.ReadAllText(Path.Combine(dir, "metadata.json"))).RootElement;
        metadata.GetProperty("sha256").GetString().Should().Be(file.Sha256);
        metadata.GetProperty("fileName").GetString().Should().Be("sp200.exe");
        System.IO.File.GetLastWriteTimeUtc(Path.Combine(dir, "metadata.json")).Should().BeOnOrAfter(System.IO.File.GetLastWriteTimeUtc(Path.Combine(dir, "sp200.exe")));
    }

    [Fact]
    public async Task AHashMismatchWhileCopying_FailsWithTheFix_AndRemovesTheEntry()
    {
        var file = Source("softpaq") with { Sha256 = Sha("expected") };
        var cache = Path.Combine(_root, "E", "CloudImagingCache");

        var act = () => DriverSetPreloadCopier.CopyAsync(cache, [file], null);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("A driver file was damaged while copying to the USB drive. Prepare the drive again.");
        Directory.Exists(Path.Combine(cache, "drivers", file.Sha256)).Should().BeFalse();
    }

    // Manifest

    [Fact]
    public void TheManifest_RecordsEachSet_AndPreloadingPinsNothing()
    {
        var modelId = Guid.NewGuid();
        var manifest = new UsbPreparationManifest
        {
            ManifestVersion = UsbPreparationManifest.ManifestSchemaVersion,
            ToolVersion = "1",
            BootImageVersion = "1",
            SelectedDiskId = "1",
            DriverSets = [new UsbPreparationDriverSet { ModelId = modelId, ModelName = "HP EliteBook 840 G11", Release = WindowsRelease.Windows11Version24H2, ReleaseName = "Windows 11, version 24H2", SetVersion = 7, Components = 26, Bytes = 4 * Gb }],
        };

        var json = JsonDocument.Parse(JsonSerializer.Serialize(manifest)).RootElement.GetProperty("DriverSets")[0];

        json.GetProperty("ModelName").GetString().Should().Be("HP EliteBook 840 G11");
        json.GetProperty("Release").GetString().Should().Be("Windows11_24H2");
        json.GetProperty("SetVersion").GetInt32().Should().Be(7);
        json.GetProperty("Components").GetInt32().Should().Be(26);
        json.GetProperty("Bytes").GetInt64().Should().Be(4 * Gb);
    }

    [Fact]
    public async Task TheLinksRequest_OnlyReads_TheChosenSets()
    {
        var server = new FileServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"sets":[],"files":[]}""", Encoding.UTF8, "application/json") });
        var api = new OperatorApiClient(new HttpClient(server) { BaseAddress = new Uri("https://operator.test") }, NullLogger<OperatorApiClient>.Instance);

        await api.GetDriverSetLinksAsync([new MediaBuilderDriverSetRef { ModelId = Guid.NewGuid(), Release = WindowsRelease.Windows11Version24H2 }]);

        server.Requests.Should().Equal("https://operator.test/api/media-builder/driver-sets/links");
    }

    private static PrepareStorageDeviceViewModel CreateViewModel()
    {
        var http = new HttpClient();
        var provisioner = new UsbPartitionProvisioningService(NullLogger<UsbPartitionProvisioningService>.Instance);
        var deployer = new BootImageDeploymentService(NullLogger<BootImageDeploymentService>.Instance);
        return new PrepareStorageDeviceViewModel(
            new OperatorApiClient(http, NullLogger<OperatorApiClient>.Instance),
            new EntraAuthenticationService("test-client", "test-tenant", "api://test-client/.default", NullLogger<EntraAuthenticationService>.Instance),
            new UsbSafetyValidationService(NullLogger<UsbSafetyValidationService>.Instance),
            new BootImageDownloadService(http, NullLogger<BootImageDownloadService>.Instance),
            new UsbPreparationService(NullLogger<UsbPreparationService>.Instance, provisioner, deployer),
            new BootImageCacheService(Path.Combine(Path.GetTempPath(), $"ci-cache-tests-{Guid.NewGuid():N}"), NullLogger<BootImageCacheService>.Instance),
            navigateBack: () => { });
    }
}
