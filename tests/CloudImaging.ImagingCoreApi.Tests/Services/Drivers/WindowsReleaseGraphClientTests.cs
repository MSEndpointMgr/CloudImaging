using System.Net;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CloudImaging.Contracts.Enums;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

/// <summary>Reading Microsoft's feature update catalog through Microsoft Graph.</summary>
public sealed class WindowsReleaseGraphClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    private const string FeatureUpdate = "#microsoft.graph.windowsUpdates.featureUpdateCatalogEntry";

    private static string Entry(string? version, string? displayName = null, string? until = "2028-10-10T00:00:00Z", string type = FeatureUpdate) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["@odata.type"] = type,
            ["id"] = Guid.NewGuid().ToString(),
            ["displayName"] = displayName ?? version,
            ["version"] = version,
            ["buildNumber"] = "26200",
            ["releaseDateTime"] = "2026-09-30T00:00:00Z",
            ["deployableUntilDateTime"] = until,
        });

    private static string Page(string? nextLink, params string[] entries) =>
        $$"""{"value":[{{string.Join(",", entries)}}]{{(nextLink is null ? string.Empty : $",\"@odata.nextLink\":\"{nextLink}\"")}}}""";

    private static WindowsReleaseGraphClient Client(HttpMessageHandler handler, TokenCredential? credential = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/") }, credential ?? new StubTokenCredential(), new ManualTimeProvider(Now));

    [Fact]
    public void ParsePage_KeepsDeployableFeatureUpdates_WithTheirBuildAndReleaseDate()
    {
        using var page = JsonDocument.Parse(Page(null,
            Entry("Windows 11, version 26H2"),
            Entry("Windows 11, version 22H2", until: "2024-10-08T00:00:00Z"),
            Entry("Windows 11, version 25H2", until: null),
            Entry(null, displayName: "Windows 11, version 24H2"),
            Entry("Windows 11", displayName: "Windows 11"),
            Entry("Windows 11, version 26H2", type: "#microsoft.graph.windowsUpdates.qualityUpdateCatalogEntry")));

        var entries = WindowsReleaseGraphClient.ParsePage(page.RootElement, Now).ToList();

        entries.Select(e => e.Release.WireName).Should().Equal("Windows11_26H2", "Windows11_25H2", "Windows11_24H2");
        entries[0].Build.Should().Be("26200");
        entries[0].ReleasedAt.Should().Be(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Read_AsksForFeatureUpdatesAsTheManagedIdentity_AndFollowsGraphsNextLinks()
    {
        var handler = new PagedHandler(
            Page("https://graph.microsoft.com/beta/admin/windows/updates/catalog/entries?$skiptoken=2", Entry("Windows 11, version 25H2")),
            Page("https://attacker.example/steal", Entry("Windows 11, version 26H2")));

        var read = await Client(handler).ReadAsync();

        read.Problem.Should().BeNull();
        read.Releases.Select(e => e.Release.WireName).Should().Equal("Windows11_25H2", "Windows11_26H2");
        handler.Requests.Should().HaveCount(2);
        Uri.UnescapeDataString(handler.Requests[0].Uri.PathAndQuery).Should().Be("/beta/admin/windows/updates/catalog/entries?$filter=isof('microsoft.graph.windowsUpdates.featureUpdateCatalogEntry')");
        handler.Requests[1].Uri.Host.Should().Be("graph.microsoft.com");
        handler.Requests.Should().OnlyContain(r => r.Authorization == "Bearer test-token");
    }

    [Fact]
    public async Task Read_Forbidden_NamesThePermissionAndLicence()
    {
        var read = await Client(new PagedHandler(HttpStatusCode.Forbidden, """{"error":{"message":"Forbidden"}}""")).ReadAsync();

        read.Releases.Should().BeEmpty();
        read.Problem.Should().Be(WindowsReleaseGraphClient.DeniedMessage).And.Contain("WindowsUpdates.Read.All");
    }

    [Fact]
    public async Task Read_AnotherError_CarriesGraphsMessage()
    {
        var read = await Client(new PagedHandler(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid filter clause"}}""")).ReadAsync();

        read.Problem.Should().Be("Microsoft Graph could not list the Windows releases (HTTP 400): Invalid filter clause");
    }

    [Fact]
    public async Task Read_WhenTheIdentityCannotSignIn_IsAProblem_NotAnException()
    {
        var read = await Client(new PagedHandler(), new FailingTokenCredential()).ReadAsync();

        read.Releases.Should().BeEmpty();
        read.Problem.Should().StartWith("The Imaging Core managed identity could not sign in to Microsoft Graph");
    }

    [Fact]
    public async Task Read_WhenGraphIsUnreachable_IsAProblem_NotAnException()
    {
        var read = await Client(new PagedHandler(new HttpRequestException("No such host is known."))).ReadAsync();

        read.Problem.Should().Be("Microsoft Graph could not be reached: No such host is known.");
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private static readonly AccessToken Token = new("test-token", DateTimeOffset.MaxValue);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Token;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }

    private sealed class FailingTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("No managed identity endpoint found.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new AuthenticationFailedException("No managed identity endpoint found.");
    }

    /// <summary>Serves the pages in order, or one status for every request, or throws.</summary>
    private sealed class PagedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _pages = new();
        private readonly HttpStatusCode _status = HttpStatusCode.OK;
        private readonly string? _body;
        private readonly Exception? _failure;

        public PagedHandler(params string[] pages)
        {
            foreach (var page in pages)
            {
                _pages.Enqueue(page);
            }
        }

        public PagedHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public PagedHandler(Exception failure)
        {
            _failure = failure;
        }

        public List<(Uri Uri, string? Authorization)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString()));
            if (_failure is not null)
            {
                throw _failure;
            }

            var body = _body ?? (_pages.Count > 0 ? _pages.Dequeue() : """{"value":[]}""");
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(body) });
        }
    }
}
