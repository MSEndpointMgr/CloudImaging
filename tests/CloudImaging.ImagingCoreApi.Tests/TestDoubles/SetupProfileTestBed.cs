using Azure.Data.Tables;
using Azure.Storage.Blobs;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.ImagingCoreApi.Tests.TestDoubles;

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Setup profile services and repositories over in-memory storage.</summary>
internal sealed class SetupProfileTestBed
{
    public static readonly DateTimeOffset Start = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    public SetupProfileTestBed()
    {
        var (tables, table) = InMemoryTableClient.CreateService();
        Tables = tables;
        Table = table;
        Blobs = new InMemoryBlobContainerClient();
        Profiles = new SetupProfileRepository(tables);
        Targets = new SetupProfileTargetRepository(tables);
        Versions = new SetupProfileVersionRepository(tables, Blobs, Time);
        Texts = new SetupProfileContentRepository(Blobs);
        History = new SetupProfileHistoryRepository(tables);
        Locations = new LocationRepository(tables);
        Sessions = new DeviceSessionRepository(tables);
        Steps = new ImagingStepRepository(tables);
        Items = new ImagingStepItemRepository(tables);
        Config = new PortalConfigurationRepository(tables, NullLogger<PortalConfigurationRepository>.Instance);
        Service = new SetupProfileService(Profiles, Targets, Versions, Texts, History, Locations, Sessions, Config, Time, NullLogger<SetupProfileService>.Instance);
        Resolver = new SetupProfileResolver(Targets, Profiles);
        Stockholm = AddLocation("Stockholm HQ");
        Gothenburg = AddLocation("Gothenburg Office");
        Malmo = AddLocation("Malmo Lab");
    }

    public TableServiceClient Tables { get; }

    public Func<string, InMemoryTableClient> Table { get; }

    public InMemoryBlobContainerClient Blobs { get; }

    /// <summary>Signs SAS URLs offline with the well-known development account key.</summary>
    public BlobServiceClient BlobService { get; } = new("UseDevelopmentStorage=true");

    public ManualTimeProvider Time { get; } = new(Start);

    public SetupProfileRepository Profiles { get; }

    public SetupProfileTargetRepository Targets { get; }

    public SetupProfileVersionRepository Versions { get; }

    public SetupProfileContentRepository Texts { get; }

    public SetupProfileHistoryRepository History { get; }

    public LocationRepository Locations { get; }

    public DeviceSessionRepository Sessions { get; }

    public ImagingStepRepository Steps { get; }

    public ImagingStepItemRepository Items { get; }

    public PortalConfigurationRepository Config { get; }

    public SetupProfileService Service { get; }

    public SetupProfileResolver Resolver { get; }

    public Guid Stockholm { get; }

    public Guid Gothenburg { get; }

    public Guid Malmo { get; }

    public static SetupProfileActor Actor(string upn = "ops@contoso.com") => new() { Upn = upn, ObjectId = "00000000-0000-0000-0000-000000000001" };

    public static SetupProfileTargeting AllDevices => new() { AllDevices = true };

    public static SetupProfileTargeting At(params Guid[] locations) => new() { LocationIds = locations };

    public static SetupRegistryValue Value(string name = "Stop automatic device encryption", string data = "1", Guid? id = null) => new()
    {
        Id = id,
        Name = name,
        Hive = SetupRegistryHive.System,
        Key = "CurrentControlSet\\Control\\BitLocker",
        ValueName = name.Replace(' ', '_'),
        Type = SetupRegistryValueType.DWord,
        Data = data,
    };

    public static SetupProfileContent Content(params SetupRegistryValue[] values) => new()
    {
        RegistryValues = values.Length == 0 ? [Value()] : values,
    };

    /// <summary>Lowercase hex SHA-256 of a text's UTF-8 bytes.</summary>
    public static string Sha(string text) => SetupProfileContentRepository.Sha256(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Stores a text as an upload would and returns its hash.</summary>
    public string Upload(string text, string kind = SetupProfileContentRepository.ScriptKind) =>
        Texts.StoreAsync(System.Text.Encoding.UTF8.GetBytes(text), kind, Time.GetUtcNow()).GetAwaiter().GetResult().Sha256;

    /// <summary>A script whose text is uploaded first.</summary>
    public SetupScript Script(string name = "Enable Wake on LAN", string text = "exit 0", Guid? id = null, int order = 1, SetupScriptPhase runs = SetupScriptPhase.WinPE, params Guid[] locations) => new()
    {
        Id = id,
        Name = name,
        Runs = runs,
        Order = order,
        TimeoutMinutes = 5,
        ContentSha256 = Upload(text),
        Locations = locations.Length == 0 ? new SetupScriptLocations() : new SetupScriptLocations { All = false, LocationIds = locations },
    };

    /// <summary>An answer file whose text is uploaded first.</summary>
    public SetupAnswerFile AnswerFile(string xml) => new() { ContentSha256 = Upload(xml, SetupProfileContentRepository.AnswerFileKind) };

    public async Task<SetupProfileDetail> CreateAsync(string name, SetupProfileTargeting targeting, SetupProfileContent? content = null, string upn = "ops@contoso.com")
    {
        var result = await Service.CreateAsync(new CreateSetupProfileRequest
        {
            Name = name,
            Targeting = targeting,
            Content = content ?? Content(),
            Actor = Actor(upn),
        });
        if (result.Outcome != SetupProfileOutcome.Created)
        {
            throw new InvalidOperationException($"Create failed: {result.Outcome} {result.Detail}");
        }
        return (SetupProfileDetail)result.Value!;
    }

    public Task<SetupProfileResult> SaveAsync(SetupProfileDetail detail, SetupProfileContent? content = null, SetupProfileTargeting? targeting = null, string? name = null, string upn = "ops@contoso.com") =>
        Service.SaveAsync(detail.ProfileId, new SaveSetupProfileRequest
        {
            BaseVersion = detail.CurrentVersion,
            Name = name ?? detail.Name,
            Targeting = targeting ?? detail.Targeting,
            Content = content,
            Actor = Actor(upn),
        });

    public async Task<DeviceSession> AddSessionAsync(SessionState state, Guid? locationId = null, Guid? profileId = null, int? version = null)
    {
        var session = new DeviceSession
        {
            SessionId = Guid.NewGuid(),
            State = state,
            DeviceSerialNumber = "SN-1",
            DeviceManufacturer = "HP",
            DeviceModel = "EliteBook",
            LocationId = locationId,
            SetupProfileId = profileId,
            SetupProfileVersion = version,
            SetupProfileName = profileId is null ? null : "pinned",
            CreatedAt = Start,
        };
        await Sessions.CreateAsync(session);
        return session;
    }

    private Guid AddLocation(string name)
    {
        var id = Guid.NewGuid();
        Locations.CreateAsync(new Location { LocationId = id, Name = name, CountryCode = "SE" }).GetAwaiter().GetResult();
        return id;
    }
}
