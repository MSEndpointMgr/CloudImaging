using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudImaging.Client.Services;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Client.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Loading the session's setup profile before Format disk: no profile means no work, a pinned
/// version is downloaded without the device token and checked by length and SHA-256, and three
/// failed attempts fail the session with a message the technician can act on.
/// </summary>
public sealed class SetupProfileContentLoaderTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private const string ContentUrl = "https://corestorage.blob.core.windows.net/setup-profiles/p/000008.json?sv=2024&sig=secret";

    private static byte[] Document(int schemaVersion = 1) => Encoding.UTF8.GetBytes(
        $$"""{"schemaVersion":{{schemaVersion}},"version":8,"failureHandling":"CompleteWithWarnings","registryValues":[{"id":"7d0e5a1c-0000-4000-8000-000000000001","name":"Time zone","hive":"System","key":"CurrentControlSet\\Services\\tzautoupdate","valueName":"Start","type":"DWord","data":"3"}],"scripts":[],"answerFile":null}""");

    private static string Descriptor(byte[] document, string? sha = null, long? size = null) => JsonSerializer.Serialize(new
    {
        profileId = Guid.NewGuid(),
        profileName = "Stockholm",
        version = 8,
        failureHandling = "CompleteWithWarnings",
        contentUrl = ContentUrl,
        contentUrlExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        contentSha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(document)),
        contentSizeBytes = size ?? document.Length,
    });

    private sealed class Harness
    {
        public required RecordingHandler Gateway { get; init; }
        public required RecordingHandler Download { get; init; }
        public required SetupProfileContentLoader Loader { get; init; }
        public required List<TimeSpan> Waits { get; init; }
        public required ListLogger Logger { get; init; }
    }

    private static Harness Create(Func<int, HttpResponseMessage> gateway, Func<int, HttpResponseMessage>? download = null)
    {
        var gatewayCalls = 0;
        var downloadCalls = 0;
        var gatewayHandler = new RecordingHandler(_ => gateway(++gatewayCalls));
        var downloadHandler = new RecordingHandler(_ => download is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : download(++downloadCalls));
        var http = new HttpClient(gatewayHandler) { BaseAddress = new Uri("https://gateway.example.com") };
        var client = new DeviceGatewayApiClient(http);
        client.SetSessionToken("device-token");
        var waits = new List<TimeSpan>();
        var logger = new ListLogger();
        var loader = new SetupProfileContentLoader(client, new HttpClient(downloadHandler), logger, (wait, _) => { waits.Add(wait); return Task.CompletedTask; });
        return new Harness { Gateway = gatewayHandler, Download = downloadHandler, Loader = loader, Waits = waits, Logger = logger };
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task NoPin_MeansNoProfileWork(HttpStatusCode status)
    {
        var h = Create(_ => new HttpResponseMessage(status));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.None);
        h.Download.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Pinned_DownloadsWithoutTheDeviceToken_AndVerifies()
    {
        var document = Document();
        var h = Create(_ => Json(Descriptor(document)), _ => Bytes(document));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Loaded);
        result.Content!.RegistryValues.Should().ContainSingle().Which.Name.Should().Be("Time zone");
        result.Descriptor!.Version.Should().Be(8);
        h.Gateway.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be($"/api/v1/sessions/{SessionId}/setup-profile");
        var download = h.Download.Requests.Single().Request;
        download.RequestUri!.ToString().Should().Be(ContentUrl);
        download.Headers.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task HashMismatch_RequestsANewDescriptor()
    {
        var document = Document();
        var h = Create(_ => Json(Descriptor(document)), call => Bytes(call == 1 ? Encoding.UTF8.GetBytes(new string('x', document.Length)) : document));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Loaded);
        h.Gateway.Requests.Should().HaveCount(2);
        h.Download.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task LargerDocument_IsRefused_AfterReadingOneExtraByte()
    {
        var document = Document();
        var h = Create(_ => Json(Descriptor(document)), _ => Bytes([.. document, .. new byte[1000]]));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Failed);
        result.FailureMessage.Should().Be("The setup profile for this device could not be downloaded. Check the device's network connection and start imaging again.");
        h.Download.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ThreeFailedAttempts_FailWithTheDownloadMessage()
    {
        var h = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Failed);
        result.FailureMessage.Should().Be(SetupProfileContentLoader.DownloadFailedMessage);
        h.Gateway.Requests.Should().HaveCount(3);
        h.Waits.Should().HaveCount(2, "there is no wait after the last attempt");
    }

    [Fact]
    public async Task MalformedDescriptor_IsRetried_ThenFailsWithTheDownloadMessage()
    {
        var h = Create(_ => Json("{ not json"));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Failed);
        result.FailureMessage.Should().Be(SetupProfileContentLoader.DownloadFailedMessage);
        h.Gateway.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task RateLimited_WaitsRetryAfter_AndCountsAsAnAttempt()
    {
        var document = Document();
        var h = Create(call =>
        {
            if (call == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return limited;
            }
            return Json(Descriptor(document));
        }, _ => Bytes(document));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Loaded);
        h.Waits.Should().Equal(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task NewerSchema_FailsWithTheUpdateMessage()
    {
        var document = Document(schemaVersion: 2);
        var h = Create(_ => Json(Descriptor(document)), _ => Bytes(document));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Failed);
        result.FailureMessage.Should().Be("This setup profile needs a newer Cloud Imaging Client. Update the boot image and start imaging again.");
    }

    [Fact]
    public async Task Conflict_MeansThePipelineIsEnding()
    {
        var h = Create(_ => new HttpResponseMessage(HttpStatusCode.Conflict));

        (await h.Loader.LoadAsync(SessionId)).Status.Should().Be(SetupProfileLoadStatus.PipelineEnding);
    }

    [Fact]
    public async Task TheReadUrl_IsNeverLogged_OnlyItsHost()
    {
        var document = Document();
        var h = Create(_ => Json(Descriptor(document)), call => call == 1 ? new HttpResponseMessage(HttpStatusCode.Forbidden) : Bytes(document));

        await h.Loader.LoadAsync(SessionId);

        h.Logger.Messages.Should().NotContain(m => m.Contains("sig=", StringComparison.Ordinal) || m.Contains("000008.json", StringComparison.Ordinal));
        h.Logger.Messages.Should().Contain(m => m.Contains("corestorage.blob.core.windows.net", StringComparison.Ordinal));
    }

    internal sealed class ListLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
