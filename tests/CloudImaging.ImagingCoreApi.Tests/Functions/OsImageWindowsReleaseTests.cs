using System.Net;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>The OS image's Windows release, which decides the driver set a device gets.</summary>
public sealed class OsImageWindowsReleaseTests
{
    private readonly OsImageRepository _images;
    private readonly ImageCatalogFunctions _functions;
    private readonly Guid _imageId = Guid.NewGuid();

    public OsImageWindowsReleaseTests()
    {
        var (tables, _) = InMemoryTableClient.CreateService();
        _images = new OsImageRepository(tables);
        var sessions = new DeviceSessionRepository(tables);
        _functions = new ImageCatalogFunctions(_images, sessions, new ImageDeletionGuardService(sessions, NullLogger<ImageDeletionGuardService>.Instance), NullLogger<ImageCatalogFunctions>.Instance);
        _images.CreateAsync(new OsImage { ImageId = _imageId, Name = "Windows 11", Version = "24H2", StoragePath = "os-images/w11.wim", Sha256Hash = new string('a', 64) }).GetAwaiter().GetResult();
    }

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> PatchAsync(object body) =>
        _functions.UpdateImage(FunctionTestHost.Request("PATCH", $"https://core/api/internal/images/{_imageId}", body), _imageId.ToString(), FunctionTestHost.Context);

    [Fact]
    public async Task Patch_SetsTheRelease_ReturnsItAsTheWireValue_AndStoresIt()
    {
        var response = await PatchAsync(new { windowsRelease = "Windows11_24H2" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.ReadJsonAsync()).GetProperty("windowsRelease").GetString().Should().Be("Windows11_24H2");
        (await _images.GetByIdAsync(_imageId))!.WindowsRelease.Should().Be(WindowsRelease.Windows11Version24H2);
    }

    [Fact]
    public async Task Patch_WithoutTheField_KeepsTheRelease_AndNullClearsIt()
    {
        await PatchAsync(new { windowsRelease = "Windows11_24H2" });

        await PatchAsync(new { name = "Windows 11 Enterprise" });
        (await _images.GetByIdAsync(_imageId))!.WindowsRelease.Should().Be(WindowsRelease.Windows11Version24H2);

        await PatchAsync(new { windowsRelease = (string?)null });
        (await _images.GetByIdAsync(_imageId))!.WindowsRelease.Should().BeNull();
    }

    [Fact]
    public async Task Patch_UnknownRelease_Is400_AndChangesNothing()
    {
        var response = await PatchAsync(new { windowsRelease = "Windows12_26H2" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadBodyAsync()).Should().Be("Unknown Windows release.");
        (await _images.GetByIdAsync(_imageId))!.WindowsRelease.Should().BeNull();
    }

    [Fact]
    public async Task UploadStart_RefusesAnUnknownRelease_BeforeAnythingIsStaged()
    {
        // Storage, jobs and validation are never reached for a refused request.
        var uploads = new OsImageUploadFunctions(_images, null!, null!, null!, NullLogger<OsImageUploadFunctions>.Instance);

        var response = await uploads.StartUpload(FunctionTestHost.Request("POST", "https://core/api/internal/images/upload/start", new { name = "win11.wim", version = "24H2", sha256Hash = new string('a', 64), windowsRelease = "Windows12_26H2" }), FunctionTestHost.Context);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadBodyAsync()).Should().Be("Unknown Windows release.");
    }
}
