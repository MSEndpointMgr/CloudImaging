using System.Security.Cryptography;
using System.Text.Json;
using Azure.Storage.Blobs;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.ImagingCoreApi.Tests.TestDoubles;

/// <summary>
/// A vendor adapter whose catalogs are JSON documents served by <see cref="FakeVendorHandler"/>,
/// so sync tests control exactly which models and components a vendor publishes.
/// </summary>
internal sealed class FakeDriverVendorAdapter(DriverVendor vendor = DriverVendor.Hp) : IDriverVendorAdapter
{
    public DriverVendor Vendor => vendor;

    public string MatchKeyType => vendor == DriverVendor.Hp ? "system ID" : "system SKU";

    public string ModelListUrl => vendor == DriverVendor.Hp ? "https://hpia.hpcloud.hp.com/ref/platformList.cab" : $"https://downloads.dell.com/fake/{vendor}/models.json";

    public static string ComponentCatalogUrl(string matchKey, WindowsRelease release) =>
        $"https://hpia.hpcloud.hp.com/ref/{matchKey.ToLowerInvariant()}/{release}.json";

    /// <summary>This vendor's component catalog for a match key; HP's is <see cref="ComponentCatalogUrl"/>.</summary>
    public string CatalogUrl(string matchKey, WindowsRelease release) =>
        vendor == DriverVendor.Hp ? ComponentCatalogUrl(matchKey, release) : $"https://downloads.dell.com/fake/{vendor}/{matchKey.ToLowerInvariant()}/{release}.json";

    public string? DeviceMatchKey(DeviceHardwareMetadata? hardware) => vendor == DriverVendor.Hp ? hardware?.MotherboardModel : hardware?.SystemSku;

    public bool IsVendorOf(string? manufacturer) => manufacturer?.Contains(vendor == DriverVendor.Hp ? "HP" : vendor.ToString(), StringComparison.OrdinalIgnoreCase) == true;

    public IReadOnlyList<DriverVendorModel> ParseModelList(Stream content, DateTimeOffset seenAt) =>
        JsonSerializer.Deserialize<List<DriverVendorModel>>(content, DriverJson.Options)!
            .Select(m => m with { Model = m.Model with { SeenAt = seenAt } })
            .ToList();

    public IReadOnlyList<string> ComponentCatalogUrls(DriverCatalogModelRecord model, WindowsRelease release) =>
        [CatalogUrl(model.MatchKeys[0], release)];

    public IReadOnlyList<DriverVendorComponent> ParseComponents(Stream content, DriverCatalogModelRecord model, WindowsRelease release, DateTimeOffset seenAt) =>
        JsonSerializer.Deserialize<List<DriverVendorComponent>>(content, DriverJson.Options)!;
}

/// <summary>
/// The sync engine over <see cref="DriverTestBed"/> with the fake adapter, and a pump that runs
/// queued messages the way the host would (delayed messages are held back).
/// </summary>
internal sealed class DriverSyncHarness
{
    public const WindowsRelease Release = WindowsRelease.Windows11Version24H2;

    public DriverSyncHarness(params FakeDriverVendorAdapter[] otherVendors)
    {
        OtherVendors = otherVendors;
        Storage = new DriverStorageService(new BlobServiceClient("UseDevelopmentStorage=true"), Bed.Blobs, Bed.Http, Bed.Time);
        Sessions = new DeviceSessionRepository(Bed.Tables);
        Catalogs = new DriverCatalogService(Bed.Store, Bed.Models, Storage, Bed.Blobs, [Adapter, .. otherVendors], Bed.Time);
        Sync = new DriverSyncService(Bed.Store, Bed.Models, Bed.Pointers, Bed.Runs, Bed.SetDocuments, Catalogs, Storage, Bed.Work, Sessions, Bed.Time, NullLogger<DriverSyncService>.Instance);
        ModelService = new DriverModelService(Bed.Store, Bed.Models, Bed.Pointers, Bed.Runs, Bed.SetDocuments, Catalogs, Sync, Bed.Work, Bed.Time);
        PolicyService = new DriverPolicyService(Bed.Store, Bed.Models, Bed.Pointers, ModelService, Bed.Work, Bed.Time);
        Scheduler = new DriverSyncScheduler(Bed.Store, Sync, Bed.Work, Bed.Time, NullLogger<DriverSyncScheduler>.Instance);
        Lifecycle = new DriverLifecycleService(Bed.Store, Bed.Models, Bed.Pointers, Bed.SetDocuments, Sessions, Sync, ModelService, PolicyService, Bed.Time);
        History = new DriverComponentHistoryService(Bed.Store, Bed.Models, Storage, ModelService, PolicyService, Bed.Time);
        Custom = new DriverCustomComponentService(Bed.Store, Bed.Models, Storage, PolicyService, Bed.Time);
    }

    public DriverCustomComponentService Custom { get; }

    public DriverComponentHistoryService History { get; }

    public DriverLifecycleService Lifecycle { get; }

    public IReadOnlyList<FakeDriverVendorAdapter> OtherVendors { get; }

    public DriverSyncScheduler Scheduler { get; }

    public DriverPolicyService PolicyService { get; }

    public DriverModelService ModelService { get; }

    public DriverTestBed Bed { get; } = new();

    public FakeDriverVendorAdapter Adapter { get; } = new();

    public DriverStorageService Storage { get; }

    public DeviceSessionRepository Sessions { get; }

    public DriverCatalogService Catalogs { get; }

    public DriverSyncService Sync { get; }

    /// <summary>Messages sent with a delay, kept back from the pump.</summary>
    public List<SentQueueMessage> Delayed { get; } = [];

    private readonly List<(SentQueueMessage Sent, DateTimeOffset Due)> _due = [];

    /// <summary>Every message the pump ran, in order.</summary>
    public List<DriverWorkMessage> Handled { get; } = [];

    /// <summary>Publishes a model list.</summary>
    public void PublishModels(params DriverVendorModel[] models) => PublishModels(Adapter, models);

    /// <summary>Publishes a vendor's model list.</summary>
    public void PublishModels(FakeDriverVendorAdapter adapter, params DriverVendorModel[] models) =>
        Bed.Vendor.ServeBytes(adapter.ModelListUrl, JsonSerializer.SerializeToUtf8Bytes(models.ToList(), DriverJson.Options));

    /// <summary>Publishes a component catalog for a match key and serves each component's file.</summary>
    public void PublishComponents(string matchKey, params DriverVendorComponent[] components) => PublishComponents(Adapter, matchKey, components);

    /// <summary>Publishes a vendor's component catalog for a match key and serves each component's file.</summary>
    public void PublishComponents(FakeDriverVendorAdapter adapter, string matchKey, params DriverVendorComponent[] components)
    {
        Bed.Vendor.ServeBytes(adapter.CatalogUrl(matchKey, Release), JsonSerializer.SerializeToUtf8Bytes(components.ToList(), DriverJson.Options));
        foreach (var component in components)
        {
            Bed.Vendor.ServeBytes(component.Component.Url!, Content(component.Component.ComponentId, component.Component.Version!));
        }
    }

    /// <summary>The bytes served for a component version.</summary>
    public static byte[] Content(string componentId, string version) => System.Text.Encoding.UTF8.GetBytes($"{componentId} {version}");

    /// <summary>A vendor component whose file the fake host serves.</summary>
    public static DriverVendorComponent Component(string lineage, string componentId, string version, string name, string category = "Driver - Network", DriverAction defaultAction = DriverAction.Offline)
    {
        var bytes = Content(componentId, version);
        return new DriverVendorComponent(lineage, new DriverComponentRecord
        {
            ComponentId = componentId,
            Name = name,
            Category = category,
            Version = version,
            Size = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Url = $"https://ftp.hp.com/pub/softpaq/{componentId}.exe",
            FileName = $"{componentId}.exe",
            AllowedActions = [DriverAction.Offline, DriverAction.Skip],
            DefaultAction = defaultAction,
            Extract = DriverExtractKind.Hp,
        });
    }

    /// <summary>A catalog model entry.</summary>
    public static DriverVendorModel CatalogModel(string matchKey, string name) =>
        new($"{matchKey}_X64", new DriverCatalogModelRecord { Name = name, MatchKeys = [matchKey], Architecture = MachineArchitecture.X64, Releases = [Release] });

    /// <summary>Turns a vendor's sync on.</summary>
    public Task EnableAsync(DriverVendor vendor = DriverVendor.Hp) =>
        Bed.Store.VendorSettings.UpsertAsync(DriverStore.VendorPartition, vendor.ToString(), new DriverVendorSettingRecord { SyncEnabled = true });

    /// <summary>Stores a catalog model and onboards it.</summary>
    public async Task<DriverModelRecord> OnboardAsync(string matchKey, string name, DriverModelState state = DriverModelState.Active, DriverVendor vendor = DriverVendor.Hp)
    {
        var catalog = CatalogModel(matchKey, name);
        await Bed.Store.CatalogModels.UpsertAsync(vendor.ToString(), catalog.Key, catalog.Model);
        var model = new DriverModelRecord
        {
            ModelId = Guid.NewGuid(),
            Vendor = vendor,
            Architecture = MachineArchitecture.X64,
            DisplayName = name,
            CatalogModelKey = catalog.Key,
            MatchKeys = [matchKey],
            Releases = [Release],
            State = state,
            OnboardedAt = Bed.Time.GetUtcNow(),
            OnboardedBy = "operator@contoso.com",
            CompletedBy = state == DriverModelState.Active ? "operator@contoso.com" : null,
        };
        await Bed.Models.TryAddAsync(model);
        await Bed.Models.TryReserveKeysAsync(vendor, model.MatchKeys, model.ModelId);
        return model;
    }

    /// <summary>Runs every immediate message until the queue is empty.</summary>
    public async Task PumpAsync()
    {
        while (true)
        {
            var batch = Bed.Queues.Drain();
            if (batch.Count == 0)
            {
                return;
            }

            foreach (var sent in batch)
            {
                if (sent.Delay is { } delay && delay > TimeSpan.Zero)
                {
                    Delayed.Add(sent);
                    _due.Add((sent, Bed.Time.GetUtcNow() + delay));
                    continue;
                }

                Handled.Add(sent.Work);
                await Sync.HandleAsync(sent.Work);
            }
        }
    }

    /// <summary>Moves the clock on and runs every delayed message that is due by then, and what they queue.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        var until = Bed.Time.GetUtcNow() + by;
        while (true)
        {
            await PumpAsync();
            var due = _due.Where(d => d.Due <= until && Delayed.Any(x => ReferenceEquals(x, d.Sent))).OrderBy(d => d.Due).FirstOrDefault();
            if (due.Sent is null)
            {
                break;
            }

            Delayed.Remove(due.Sent);
            _due.Remove(due);
            if (due.Due > Bed.Time.GetUtcNow())
            {
                Bed.Time.Advance(due.Due - Bed.Time.GetUtcNow());
            }

            Handled.Add(due.Sent.Work);
            await Sync.HandleAsync(due.Sent.Work);
        }

        if (until > Bed.Time.GetUtcNow())
        {
            Bed.Time.Advance(until - Bed.Time.GetUtcNow());
        }
    }

    /// <summary>Starts a run and runs it to the end.</summary>
    public async Task<DriverSyncRunRecord> RunAsync(DriverSyncTrigger trigger = DriverSyncTrigger.Manual, Guid? modelId = null)
    {
        var start = await Sync.StartRunAsync(trigger, "operator@contoso.com", modelId);
        start.Status.Should().Be(DriverRunStartStatus.Started, start.Detail);
        await PumpAsync();
        return (await Bed.Runs.GetAsync(start.RunId!.Value))!.Value;
    }

    /// <summary>The current set document of a model.</summary>
    public async Task<DriverSetDocument?> CurrentSetAsync(Guid modelId)
    {
        var pointer = await Bed.Pointers.GetAsync(modelId, Release);
        return pointer?.Value.CurrentVersion is { } version ? await Bed.SetDocuments.GetAsync(modelId, Release, version) : null;
    }

    /// <summary>A model's component rows.</summary>
    public async Task<Dictionary<string, DriverComponentRecord>> ComponentsAsync(Guid modelId) =>
        (await Bed.Store.Components.ListPartitionAsync(DriverTables.Pair(modelId, Release))).ToDictionary(r => r.RowKey, r => r.Value);
}
