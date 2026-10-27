using System.Net;
using Azure.Data.Tables;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;

namespace CloudImaging.ImagingCoreApi.Tests.TestDoubles;

/// <summary>
/// Serves vendor URLs from the fixture folder, so driver tests never reach a vendor host. Extra
/// responses (redirects, failures, arbitrary bytes) can be added per URL.
/// </summary>
internal sealed class FakeVendorHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);

    public List<Uri> Requests { get; } = [];

    /// <summary>Serves a fixture file at <paramref name="url"/>.</summary>
    public FakeVendorHandler ServeFixture(string url, string fixtureName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "drivers", fixtureName);
        _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(path)) };
        return this;
    }

    /// <summary>Serves bytes at <paramref name="url"/>.</summary>
    public FakeVendorHandler ServeBytes(string url, byte[] bytes)
    {
        _responses[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        return this;
    }

    /// <summary>Answers <paramref name="url"/> with a status and optional redirect location.</summary>
    public FakeVendorHandler ServeStatus(string url, HttpStatusCode status, string? location = null)
    {
        _responses[url] = () =>
        {
            var response = new HttpResponseMessage(status);
            if (location is not null)
            {
                response.Headers.Location = new Uri(location);
            }

            return response;
        };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var response = _responses.TryGetValue(request.RequestUri!.ToString(), out var factory)
            ? factory()
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

/// <summary>Driver repositories and services over in-memory storage, a manual clock and fixture-backed vendor hosts.</summary>
internal sealed class DriverTestBed
{
    public static readonly DateTimeOffset Start = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    public DriverTestBed()
    {
        var (tables, table) = InMemoryTableClient.CreateService();
        Tables = tables;
        Table = table;
        Store = new DriverStore(tables);
        Models = new DriverModelRepository(tables);
        Pointers = new DriverSetPointerRepository(tables);
        Runs = new DriverSyncRunRepository(tables);
        SetDocuments = new DriverSetDocumentRepository(Blobs);
        Work = new DriverWorkSender(Queues.Service, Time);
        Http = new HttpClient(Vendor, disposeHandler: false);
    }

    public TableServiceClient Tables { get; }

    public Func<string, InMemoryTableClient> Table { get; }

    public InMemoryBlobContainerClient Blobs { get; } = new(DriverTables.Container);

    public InMemoryQueues Queues { get; } = new();

    public FakeVendorHandler Vendor { get; } = new();

    public HttpClient Http { get; }

    public ManualTimeProvider Time { get; } = new(Start);

    public DriverStore Store { get; }

    public DriverModelRepository Models { get; }

    public DriverSetPointerRepository Pointers { get; }

    public DriverSyncRunRepository Runs { get; }

    public DriverSetDocumentRepository SetDocuments { get; }

    public DriverWorkSender Work { get; }
}
