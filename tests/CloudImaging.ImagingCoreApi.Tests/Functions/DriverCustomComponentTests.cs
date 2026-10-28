using System.IO.Compression;
using System.Net;
using System.Text;
using Azure.Storage.Blobs.Specialized;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker.Http;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>Custom components: staged upload, add, replace a vendor component, notices, removal and clean-up.</summary>
public sealed class DriverCustomComponentTests
{
    private const string Base = "https://core.test/api/internal/drivers/custom-components";
    private const string Upn = "operator@contoso.com";
    private static readonly string[] AnyBlock = ["AA=="];
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private readonly DriverSyncHarness _h = new();
    private readonly DriverCustomComponentFunctions _functions;
    private DriverModelRecord _model = null!;

    public DriverCustomComponentTests()
    {
        _functions = new DriverCustomComponentFunctions(_h.Custom);
        _h.PublishModels(DriverSyncHarness.CatalogModel("8C26", "HP EliteBook 840 G11"));
        Publish("1.0");
    }

    private void Publish(string wireless) =>
        _h.PublishComponents(
            "8C26",
            DriverSyncHarness.Component("sp100", wireless == "1.0" ? "sp200" : $"sp2{wireless.Replace(".", string.Empty, StringComparison.Ordinal)}", wireless, "Intel Wireless LAN Driver"),
            DriverSyncHarness.Component("sp101", "sp201", "2.0", "Realtek Audio Driver", "Driver - Audio"));

    private async Task OnboardAsync()
    {
        await _h.EnableAsync();
        _model = await _h.OnboardAsync("8C26", "HP EliteBook 840 G11");
        await _h.RunAsync();
    }

    /// <summary>A ZIP with the given entries, built here rather than kept as a binary fixture.</summary>
    internal static byte[] Zip(params string[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write($"content of {name}");
            }
        }

        return buffer.ToArray();
    }

    private async Task<HttpResponseData> StartAsync(string fileName, long sizeBytes, string upn = Upn) =>
        await _functions.StartCustomComponentUpload(FunctionTestHost.Request("POST", $"{Base}/uploads/start", new { fileName, sizeBytes, actor = new { upn } }), FunctionTestHost.Context);

    // Starts an upload, stages the bytes in two blocks the way the browser does, and completes it.
    private async Task<(Guid UploadId, HttpResponseData Completed)> UploadAsync(string fileName, byte[] bytes, long? declared = null)
    {
        var started = await (await StartAsync(fileName, declared ?? bytes.Length)).ReadJsonAsync();
        var uploadId = started.GetProperty("uploadId").GetGuid();
        started.GetProperty("uploadUrl").GetString().Should().Contain($"custom/staging/{uploadId:N}/{fileName}");
        var blob = _h.Bed.Blobs.GetBlockBlobClient($"custom/staging/{uploadId:N}/{fileName}");
        var half = bytes.Length / 2;
        var ids = new[] { Convert.ToBase64String("000000"u8.ToArray()), Convert.ToBase64String("000001"u8.ToArray()) };
        await blob.StageBlockAsync(ids[0], new MemoryStream(bytes[..half]));
        await blob.StageBlockAsync(ids[1], new MemoryStream(bytes[half..]));
        var completed = await CompleteAsync(uploadId, ids);
        return (uploadId, completed);
    }

    private async Task<HttpResponseData> CompleteAsync(Guid uploadId, string[] blockIds, string upn = Upn) =>
        await _functions.CompleteCustomComponentUpload(FunctionTestHost.Request("POST", $"{Base}/uploads/{uploadId}/complete", new { blockIds, actor = new { upn } }), uploadId.ToString(), FunctionTestHost.Context);

    private async Task<HttpResponseData> CreateAsync(Guid uploadId, string action = "Offline", string? replaces = "sp100", string? command = null, Guid? modelId = null) =>
        await _functions.CreateCustomComponent(FunctionTestHost.Request("POST", Base, new
        {
            uploadId,
            name = "Intel Wireless LAN Driver (pre-release)",
            version = "24.0.1",
            action,
            category = "Driver - Network",
            installCommand = command,
            targets = new[] { new { modelId = modelId ?? _model.ModelId, replacesLineageKey = replaces } },
            actor = new { upn = Upn },
        }), FunctionTestHost.Context);

    [Theory]
    [InlineData("driver.txt", 100)]
    [InlineData("driver.zip", 6L * 1024 * 1024 * 1024)]
    public async Task UploadStart_RefusesOtherExtensionsAndPackagesOver5GB(string fileName, long size)
    {
        (await StartAsync(fileName, size)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UploadComplete_CommitsTheBlocks_AndReturnsTheSha256AndTheInfCount()
    {
        var bytes = Zip("x64/netwtw.inf", "x64/netwtw.sys", "x64/extra.INF");

        var (uploadId, completed) = await UploadAsync("intel.zip", bytes);

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await completed.ReadJsonAsync();
        json.GetProperty("sha256").GetString().Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
        json.GetProperty("sizeBytes").GetInt64().Should().Be(bytes.Length);
        json.GetProperty("infCount").GetInt32().Should().Be(2);
        _h.Bed.Blobs.Blobs.Keys.Should().Contain($"custom/{uploadId:N}/intel.zip");
        _h.Bed.Blobs.Blobs.Keys.Should().NotContain($"custom/staging/{uploadId:N}/intel.zip", "the upload link can only write the staging blob, which is gone");
        (await CompleteAsync(Guid.NewGuid(), AnyBlock)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_BelongsToItsUploader_AndIsRefusedWhenItsSizeIsNotTheDeclaredOne()
    {
        var bytes = Zip("x64/netwtw.inf");
        var started = await (await StartAsync("intel.zip", bytes.Length)).ReadJsonAsync();
        var uploadId = started.GetProperty("uploadId").GetGuid();
        var blob = _h.Bed.Blobs.GetBlockBlobClient($"custom/staging/{uploadId:N}/intel.zip");
        await blob.StageBlockAsync(AnyBlock[0], new MemoryStream(bytes));

        (await CompleteAsync(uploadId, AnyBlock, "someone@contoso.com")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StartAsync("intel.zip", 10, upn: string.Empty)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var (shorter, completed) = await UploadAsync("other.zip", bytes, declared: bytes.Length + 1);
        completed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _h.Bed.Blobs.Blobs.Keys.Should().NotContain(k => k.Contains(shorter.ToString("N"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_RefusesOfflineWithoutInf_SpecializeWithoutACommand_AndUnknownModels()
    {
        await OnboardAsync();
        var (noInf, _) = await UploadAsync("readme.zip", Zip("readme.txt"));
        var (installer, _) = await UploadAsync("setup.exe", Encoding.UTF8.GetBytes("MZ installer"));

        var offline = await CreateAsync(noInf);
        var specialize = await CreateAsync(installer, "Specialize", replaces: null);
        var unknown = await CreateAsync(installer, "Specialize", replaces: null, command: "setup.exe /quiet", modelId: Guid.NewGuid());

        offline.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await offline.ReadJsonAsync()).GetProperty("errors").EnumerateArray().Should().Contain(e => e.GetProperty("field").GetString() == "uploadId" && e.GetProperty("detail").GetString() == "For Offline, upload a ZIP with the driver's INF files.");
        (await specialize.ReadJsonAsync()).GetProperty("errors").EnumerateArray().Should().Contain(e => e.GetProperty("field").GetString() == "installCommand");
        (await unknown.ReadJsonAsync()).GetProperty("errors").EnumerateArray().Should().Contain(e => e.GetProperty("field").GetString() == "targets");
        (await _h.Bed.Store.CustomComponents.ListPartitionAsync(DriverStore.CustomPartition)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_ReplacesTheVendorComponent_InTheNextSet_AndDeleteRestoresIt()
    {
        await OnboardAsync();
        var (uploadId, _) = await UploadAsync("intel.zip", Zip("x64/netwtw.inf"));

        var created = await CreateAsync(uploadId);

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await created.ReadJsonAsync()).GetProperty("targets").EnumerateArray().Single().GetProperty("replacesName").GetString().Should().Be("Intel Wireless LAN Driver");
        (await _h.Bed.Pointers.GetAsync(_model.ModelId, DriverSyncHarness.Release))!.Value.Dirty.Should().BeTrue();
        await _h.AdvanceAsync(TimeSpan.FromMinutes(3));
        var set = (await _h.CurrentSetAsync(_model.ModelId))!;
        set.Items.Should().NotContain(i => i.LineageKey == "sp100");
        var custom = set.Items.Single(i => i.Origin == DriverComponentOrigin.Custom);
        custom.LineageKey.Should().Be(DriverCustomComponentService.LineageKey(uploadId));
        custom.Extract.Should().Be(DriverExtractKind.Zip);
        custom.BlobName.Should().Be($"custom/{uploadId:N}/intel.zip");
        var page = (DriverModelPageBody)await PageAsync();
        page.Rows.Single(r => r.GetProperty("lineageKey").GetString() == "sp100").GetProperty("source").GetString().Should().Be("ReplacedByCustom");
        page.Rows.Single(r => r.GetProperty("lineageKey").GetString() == "sp100").GetProperty("action").GetString().Should().Be("Skip");
        page.Rows.Single(r => r.GetProperty("source").GetString() == "Custom").GetProperty("custom").GetProperty("replacesName").GetString().Should().Be("Intel Wireless LAN Driver");
        var history = (await _h.Bed.Store.ComponentVersions.ListPartitionAsync(DriverTables.Pair(_model.ModelId, DriverSyncHarness.Release))).Select(r => r.Value);
        history.Should().Contain(v => v.Origin == DriverComponentOrigin.Custom && v.ChangedBy == $"Custom component by {Upn}");

        var deleted = await _functions.DeleteCustomComponent(FunctionTestHost.Request("DELETE", $"{Base}/{uploadId}?changedBy={Upn}"), uploadId.ToString(), FunctionTestHost.Context);

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await _h.AdvanceAsync(TimeSpan.FromMinutes(3));
        set = (await _h.CurrentSetAsync(_model.ModelId))!;
        set.Items.Should().Contain(i => i.LineageKey == "sp100");
        set.Items.Should().NotContain(i => i.Origin == DriverComponentOrigin.Custom);
        (await _h.Bed.Store.Replacements.ListPartitionAsync(_model.ModelId.ToString("D"))).Should().BeEmpty();
    }

    [Fact]
    public async Task ASecondReplacementOfOneLineage_Is409NamingTheOther()
    {
        await OnboardAsync();
        var (first, _) = await UploadAsync("intel.zip", Zip("x64/netwtw.inf"));
        var (second, _) = await UploadAsync("intel2.zip", Zip("x64/netwtw.inf"));
        (await CreateAsync(first)).StatusCode.Should().Be(HttpStatusCode.Created);

        var conflict = await CreateAsync(second);

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await conflict.ReadBodyAsync()).Should().Contain("Intel Wireless LAN Driver (pre-release) already replaces this component on HP EliteBook 840 G11.");
    }

    [Fact]
    public async Task ANewVendorVersionOfTheReplacedLineage_SetsTheNotice_AndDismissRecordsWho()
    {
        await OnboardAsync();
        var (uploadId, _) = await UploadAsync("intel.zip", Zip("x64/netwtw.inf"));
        await CreateAsync(uploadId);
        Publish("1.1");
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        await _h.RunAsync();

        var notices = ((DriverModelPageBody)await PageAsync()).Json.GetProperty("notices").EnumerateArray().ToList();

        notices.Should().ContainSingle();
        notices[0].GetProperty("version").GetString().Should().Be("1.1");
        notices[0].GetProperty("componentId").GetString().Should().Be("sp211");
        notices[0].GetProperty("vendorComponentName").GetString().Should().Be("Intel Wireless LAN Driver");
        var dismissed = await _functions.DismissCustomComponentNotice(FunctionTestHost.Request("POST", $"{Base}/{uploadId}/notices/{_model.ModelId}/dismiss", new { actor = new { upn = Upn } }), uploadId.ToString(), _model.ModelId.ToString(), FunctionTestHost.Context);
        dismissed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        ((DriverModelPageBody)await PageAsync()).Json.GetProperty("notices").GetArrayLength().Should().Be(0);
        (await _h.Bed.Store.CustomTargets.GetAsync(uploadId.ToString("N"), _model.ModelId.ToString("D")))!.Value.DismissedBy.Should().Be(Upn);
    }

    [Fact]
    public async Task CleanUp_KeepsAPackageWhileItsComponentOrAKeptSetRefersToIt_AndDeletesItAfter()
    {
        await OnboardAsync();
        var (kept, _) = await UploadAsync("intel.zip", Zip("x64/netwtw.inf"));
        await CreateAsync(kept);
        await _h.AdvanceAsync(TimeSpan.FromMinutes(3));
        await _h.Sync.CleanUpAsync();
        _h.Bed.Blobs.Blobs.Keys.Should().Contain($"custom/{kept:N}/intel.zip");

        await _functions.DeleteCustomComponent(FunctionTestHost.Request("DELETE", $"{Base}/{kept}?changedBy={Upn}"), kept.ToString(), FunctionTestHost.Context);
        await _h.AdvanceAsync(TimeSpan.FromMinutes(3));
        await _h.Sync.CleanUpAsync();
        _h.Bed.Blobs.Blobs.Keys.Should().Contain($"custom/{kept:N}/intel.zip", "the previous set still lists it");

        Publish("1.1");
        _h.Bed.Time.Advance(TimeSpan.FromHours(2));
        await _h.RunAsync();
        _h.Bed.Blobs.Blobs.Keys.Should().NotContain($"custom/{kept:N}/intel.zip");
    }

    private sealed record DriverModelPageBody(System.Text.Json.JsonElement Json)
    {
        public List<System.Text.Json.JsonElement> Rows => Json.GetProperty("components").EnumerateArray().ToList();
    }

    private async Task<object> PageAsync()
    {
        var result = await _h.ModelService.GetModelPageAsync(_model.ModelId, DriverSyncHarness.Release);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result.Body, Web);
        return new DriverModelPageBody(json);
    }
}
