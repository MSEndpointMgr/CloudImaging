using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudImaging.Client.Services;
using CloudImaging.Client.Services.Drivers;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.Contracts.Models.SetupProfiles;

namespace CloudImaging.Client.Tests.TestDoubles;

/// <summary>A clock that stands still unless moved.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The device's PCI hardware, as a test sets it.</summary>
internal sealed class FakePciInventory(params PciHardwareId[] devices) : IPciInventory
{
    public int Reads { get; private set; }

    public IReadOnlyList<PciHardwareId> Read()
    {
        Reads++;
        return devices;
    }
}

/// <summary>
/// A driver set served by fake Device Gateway and storage hosts, over a temporary folder that
/// stands in for the applied Windows volume. Nothing is written outside that folder.
/// </summary>
internal sealed class DriverStepFixture : IDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    public static readonly Guid SessionId = Guid.Parse("6f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public DriverStepFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "ci-drivers-" + Guid.NewGuid().ToString("N")) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(Root);
        Gateway = new RecordingHandler(RespondGateway);
        Storage = new RecordingHandler(RespondStorage);
        Client = new DeviceGatewayApiClient(new HttpClient(Gateway) { BaseAddress = new Uri("https://gateway.test") });
        Loader = new DriverSetLoader(Client, new HttpClient(Storage), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new FixedTimeProvider(Now), (wait, _) => { Waits.Add(wait); return Task.CompletedTask; });
    }

    public string Root { get; }

    public RecordingHandler Gateway { get; }

    public RecordingHandler Storage { get; }

    public DeviceGatewayApiClient Client { get; }

    public DriverSetLoader Loader { get; }

    public FakeRestrictedProcessRunner Processes { get; } = new();

    public List<TimeSpan> Waits { get; } = [];

    /// <summary>What the gateway answers for the descriptor.</summary>
    public Func<HttpResponseMessage> Descriptor { get; set; } = () => new HttpResponseMessage(HttpStatusCode.NotFound);

    /// <summary>What storage answers for a URL; by default the component's bytes.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?> StorageOverride { get; set; } = _ => null;

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>A component whose file storage serves.</summary>
    public SessionDriverComponent Component(int index, string componentId, DriverAction action = DriverAction.Offline, bool critical = false, DateTimeOffset? expiresAt = null, DriverExtractKind extract = DriverExtractKind.Hp)
    {
        var bytes = Encoding.UTF8.GetBytes($"softpaq {componentId}");
        var url = $"https://storage.test/drivers/files/Hp/{componentId}/1.0/{componentId}.exe?sig=x";
        Files[url] = bytes;
        return new SessionDriverComponent
        {
            Index = index,
            Name = $"Driver {componentId}",
            ComponentId = componentId,
            Version = "1.0",
            Origin = DriverComponentOrigin.Vendor,
            Category = "Driver - Network",
            Action = action,
            Critical = critical,
            SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            FileName = $"{componentId}.exe",
            DownloadUrl = url,
            DownloadUrlExpiresAt = expiresAt ?? Now.AddMinutes(60),
            Extract = extract,
        };
    }

    public void ServeDescriptor(SessionDriverSetDescriptor descriptor) =>
        Descriptor = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(descriptor, Json), Encoding.UTF8, "application/json") };

    /// <summary>Extraction writes an INF (or nothing) and any extra files, and DISM exits with the given code.</summary>
    public void ProcessesProduce(bool inf = true, int dismExitCode = 0, bool onlyLegacySwitches = false, params string[] alsoWrite)
    {
        Processes.Respond = request =>
        {
            if (request.FileName.EndsWith("dism.exe", StringComparison.OrdinalIgnoreCase))
            {
                return new RestrictedProcessResult(RestrictedProcessOutcome.Exited, dismExitCode, TimeSpan.FromSeconds(1), ["The operation completed successfully."]);
            }

            var legacy = request.Arguments.StartsWith("-pdf", StringComparison.Ordinal);
            if (!onlyLegacySwitches || legacy)
            {
                var output = legacy ? request.Arguments.Split("-f\"")[1].TrimEnd('"') : request.Arguments.Split('"')[1];
                Directory.CreateDirectory(output);
                if (inf)
                {
                    File.WriteAllText(Path.Combine(output, "netwtw.inf"), "[Version]");
                }

                foreach (var name in alsoWrite)
                {
                    File.WriteAllText(Path.Combine(output, name), $"content of {name}");
                }
            }

            return new RestrictedProcessResult(RestrictedProcessOutcome.Exited, 0, TimeSpan.FromSeconds(1), []);
        };
    }

    public DriverInstallService Service(Func<string, long>? freeSpace = null, IPciInventory? pci = null, DriverFileCache? cache = null) =>
        new(Loader, [new HpSoftPaqExtractor(Processes, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), new DellPackageExtractor(Processes, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), new ZipPackageExtractor()], new DismDriverInjector(Processes, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, freeSpace ?? (_ => long.MaxValue), pci ?? new FakePciInventory(), cache);

    public static SessionDriverSetDescriptor Set(params SessionDriverComponent[] components) =>
        new() { MatchKey = "8C26", ModelName = "HP EliteBook 840 G11", Release = WindowsRelease.Windows11Version24H2, SetVersion = 7, Components = components };

    public static ImagingStepItem Item(DriverInstallResult result, string componentId) => result.Items.Single(i => i.Target == componentId);

    private HttpResponseMessage RespondGateway(HttpRequestMessage request)
    {
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/driver-set", StringComparison.Ordinal))
        {
            return Descriptor();
        }

        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("/driver-set/components/", StringComparison.Ordinal))
        {
            var index = request.RequestUri.Segments[^2].TrimEnd('/');
            var link = new { downloadUrl = $"https://storage.test/refreshed/{index}", downloadUrlExpiresAt = Now.AddMinutes(60) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(link, Json), Encoding.UTF8, "application/json") };
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage RespondStorage(HttpRequestMessage request)
    {
        if (StorageOverride(request) is { } overridden)
        {
            return overridden;
        }

        var url = request.RequestUri!.ToString();
        if (url.StartsWith("https://storage.test/refreshed/", StringComparison.Ordinal))
        {
            url = Files.Keys.ElementAt(int.Parse(url[^1..], System.Globalization.CultureInfo.InvariantCulture));
        }

        return Files.TryGetValue(url, out var bytes)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind does no harm.
        }
    }
}
