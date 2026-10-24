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

    private static byte[] Document(int schemaVersion = 2) => Encoding.UTF8.GetBytes(
        $$"""{"schemaVersion":{{schemaVersion}},"version":8,"failureHandling":"CompleteWithWarnings","registryValues":[{"id":"7d0e5a1c-0000-4000-8000-000000000001","name":"Time zone","hive":"System","key":"CurrentControlSet\\Services\\tzautoupdate","valueName":"Start","type":"DWord","data":"3"}],"scripts":[],"answerFile":null}""");

    private static readonly Guid Everywhere = Guid.Parse("c2d40000-0000-4000-8000-000000000001");
    private static readonly Guid OsloOnly = Guid.Parse("c2d40000-0000-4000-8000-000000000002");
    private static readonly byte[] EverywhereText = Encoding.UTF8.GetBytes("Write-Output 'everywhere'");
    private static readonly byte[] OsloText = Encoding.UTF8.GetBytes("Write-Output 'oslo'");
    private static readonly byte[] AnswerText = Encoding.UTF8.GetBytes("<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"/>");

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] DocumentWithTexts() => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        version = 8,
        failureHandling = "CompleteWithWarnings",
        registryValues = Array.Empty<object>(),
        scripts = new[]
        {
            new { id = Everywhere, name = "Everywhere", runs = "WinPE", order = 1, timeoutMinutes = 5, contentSha256 = Sha(EverywhereText), sizeBytes = EverywhereText.Length, locations = new { all = true, locationIds = Array.Empty<Guid>() } },
            new { id = OsloOnly, name = "Oslo only", runs = "WinPE", order = 2, timeoutMinutes = 5, contentSha256 = Sha(OsloText), sizeBytes = OsloText.Length, locations = new { all = false, locationIds = new[] { Guid.NewGuid() } } },
        },
        answerFile = new { contentSha256 = Sha(AnswerText), sizeBytes = AnswerText.Length, passes = Array.Empty<string>() },
    }));

    private static object Text(Guid? id, byte[] bytes, string name, long? size = null) => new
    {
        id,
        contentSha256 = Sha(bytes),
        sizeBytes = size ?? bytes.Length,
        contentUrl = $"https://corestorage.blob.core.windows.net/setup-profiles/content/{name}?sig=secret",
        contentUrlExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
    };

    private static string Descriptor(byte[] document, string? sha = null, long? size = null, object[]? scripts = null, object? answerFile = null) => JsonSerializer.Serialize(new
    {
        profileId = Guid.NewGuid(),
        profileName = "Stockholm",
        version = 8,
        failureHandling = "CompleteWithWarnings",
        contentUrl = ContentUrl,
        contentUrlExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
        contentSha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(document)),
        contentSizeBytes = size ?? document.Length,
        scripts = scripts ?? [],
        answerFile,
    });

    /// <summary>Serves the document and every text by the last segment of its URL.</summary>
    private static Func<HttpRequestMessage, byte[]> Store(byte[] document, Func<string, byte[]?>? text = null) => request =>
    {
        var name = request.RequestUri!.AbsolutePath.Split('/')[^1];
        return name == "000008.json" ? document : text?.Invoke(name) ?? name switch
        {
            "everywhere" => EverywhereText,
            "oslo" => OsloText,
            "answer" => AnswerText,
            _ => [],
        };
    };

    private sealed class Harness
    {
        public required RecordingHandler Gateway { get; init; }
        public required RecordingHandler Download { get; init; }
        public required SetupProfileContentLoader Loader { get; init; }
        public required List<TimeSpan> Waits { get; init; }
        public required ListLogger Logger { get; init; }
    }

    private static Harness Create(Func<int, HttpResponseMessage> gateway, Func<int, HttpResponseMessage>? download = null, Func<HttpRequestMessage, HttpResponseMessage>? byRequest = null)
    {
        var gatewayCalls = 0;
        var downloadCalls = 0;
        var gatewayHandler = new RecordingHandler(_ => gateway(++gatewayCalls));
        var downloadHandler = new RecordingHandler(request => byRequest is not null ? byRequest(request) : download is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : download(++downloadCalls));
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
        var document = Document(schemaVersion: 3);
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
    public async Task OnlyTheListedScripts_AreVerified_WithTheAnswerFile()
    {
        var document = DocumentWithTexts();
        var h = Create(_ => Json(Descriptor(document, scripts: [Text(Everywhere, EverywhereText, "everywhere")], answerFile: Text(null, AnswerText, "answer"))), byRequest: r => Bytes(Store(document)(r)));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Loaded);
        result.Content!.Scripts.Select(s => s.Name).Should().Equal("Everywhere");
        result.AnswerFileText.Should().Be(Encoding.UTF8.GetString(AnswerText));
        h.Download.Requests.Select(r => r.Request.RequestUri!.AbsolutePath.Split('/')[^1]).Should().Equal("000008.json", "everywhere", "answer");
        h.Download.Requests.Should().OnlyContain(r => r.Request.Headers.Authorization == null);
    }

    [Fact]
    public async Task AScriptWithTheWrongHash_IsRetried_WithinTheSameThreeAttempts()
    {
        var document = DocumentWithTexts();
        var calls = 0;
        var h = Create(
            _ => Json(Descriptor(document, scripts: [Text(Everywhere, EverywhereText, "everywhere"), Text(OsloOnly, OsloText, "oslo")], answerFile: Text(null, AnswerText, "answer"))),
            byRequest: r => Bytes(Store(document, name => name == "oslo" && ++calls == 1 ? Encoding.UTF8.GetBytes("Write-Output 'OSLO'") : null)(r)));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Loaded);
        h.Gateway.Requests.Should().HaveCount(2);
        h.Download.Requests.Count(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/everywhere", StringComparison.Ordinal)).Should().Be(1, "a text verified in an earlier attempt is not checked again");
    }

    [Fact]
    public async Task AScriptThatNeverVerifies_FailsBeforeFormatDisk_WithTheDownloadMessage()
    {
        var document = DocumentWithTexts();
        var h = Create(
            _ => Json(Descriptor(document, scripts: [Text(Everywhere, EverywhereText, "everywhere", size: EverywhereText.Length - 1)], answerFile: Text(null, AnswerText, "answer"))),
            byRequest: r => Bytes(Store(document)(r)));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Failed);
        result.FailureMessage.Should().Be(SetupProfileContentLoader.DownloadFailedMessage);
        h.Gateway.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task ADescriptorListingAScriptTheDocumentDoesNotHave_Fails()
    {
        var document = DocumentWithTexts();
        var h = Create(_ => Json(Descriptor(document, scripts: [Text(Guid.NewGuid(), EverywhereText, "everywhere")], answerFile: Text(null, AnswerText, "answer"))), byRequest: r => Bytes(Store(document)(r)));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Status.Should().Be(SetupProfileLoadStatus.Failed);
        result.FailureMessage.Should().Be(SetupProfileContentLoader.DownloadFailedMessage);
    }

    [Fact]
    public async Task NoListedScripts_RunNothing_ButTheAnswerFileIsStillLoaded()
    {
        var document = DocumentWithTexts();
        var h = Create(_ => Json(Descriptor(document, answerFile: Text(null, AnswerText, "answer"))), byRequest: r => Bytes(Store(document)(r)));

        var result = await h.Loader.LoadAsync(SessionId);

        result.Content!.Scripts.Should().BeEmpty();
        result.AnswerFileText.Should().NotBeNull();
    }

    private static string ListedDescriptor(byte[] document) =>
        Descriptor(document, scripts: [Text(Everywhere, EverywhereText, "everywhere"), Text(OsloOnly, OsloText, "oslo")], answerFile: Text(null, AnswerText, "answer"));

    [Fact]
    public async Task DownloadScripts_WritesEachScriptToTheVolume_WithABom_FromAFreshDescriptor()
    {
        var document = DocumentWithTexts();
        var h = Create(_ => Json(ListedDescriptor(document)), byRequest: r => Bytes(Store(document)(r)));
        var loaded = await h.Loader.LoadAsync(SessionId);
        var volume = new FakeSetupFileSystem();

        var files = await h.Loader.DownloadScriptsAsync(SessionId, loaded, volume);

        h.Gateway.Requests.Should().HaveCount(2, "the read URLs from before Format disk have expired");
        files.Should().Equal(new Dictionary<string, string>
        {
            [Sha(EverywhereText)] = SetupProfileContentLoader.TextPath(Sha(EverywhereText)),
            [Sha(OsloText)] = SetupProfileContentLoader.TextPath(Sha(OsloText)),
        });
        files[Sha(EverywhereText)].Should().Be($@"Windows\Temp\CloudImaging\Texts\{Sha(EverywhereText)}.ps1");
        volume.Files[files[Sha(EverywhereText)]].Should().Equal([.. Encoding.UTF8.GetPreamble(), .. EverywhereText]);
        volume.Files[files[Sha(OsloText)]].Should().Equal([.. Encoding.UTF8.GetPreamble(), .. OsloText]);
        h.Download.Requests.Should().OnlyContain(r => r.Request.Headers.Authorization == null);
    }

    [Fact]
    public async Task DownloadScripts_ReportsTheScriptBeingDownloaded_AndRepeatsItOnARetry()
    {
        var document = DocumentWithTexts();
        var afterFormat = false;
        var osloCalls = 0;
        var h = Create(_ => Json(ListedDescriptor(document)), byRequest: r => Bytes(Store(document, name => afterFormat && name == "oslo" && ++osloCalls == 1 ? Encoding.UTF8.GetBytes("Write-Output 'OSLO'") : null)(r)));
        var loaded = await h.Loader.LoadAsync(SessionId);
        afterFormat = true;
        var reported = new List<(int Number, int Total)>();

        await h.Loader.DownloadScriptsAsync(SessionId, loaded, new FakeSetupFileSystem(), (number, total) => reported.Add((number, total)));

        reported.Should().Equal((1, 2), (2, 2), (2, 2));
    }

    [Fact]
    public async Task DownloadScripts_AScriptThatNeverVerifies_IsLeftOut_AndItsFileRemoved()
    {
        var document = DocumentWithTexts();
        var afterFormat = false;
        var h = Create(_ => Json(ListedDescriptor(document)), byRequest: r => Bytes(Store(document, name => afterFormat && name == "oslo" ? Encoding.UTF8.GetBytes("Write-Output 'OSLO'") : null)(r)));
        var loaded = await h.Loader.LoadAsync(SessionId);
        afterFormat = true;
        var volume = new FakeSetupFileSystem();

        var files = await h.Loader.DownloadScriptsAsync(SessionId, loaded, volume);

        files.Keys.Should().Equal(Sha(EverywhereText));
        volume.Files.Keys.Should().Equal(SetupProfileContentLoader.TextPath(Sha(EverywhereText)));
        h.Gateway.Requests.Should().HaveCount(1 + SetupProfileContentLoader.MaxAttempts);
        h.Download.Requests.Count(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/everywhere", StringComparison.Ordinal)).Should().Be(2, "a verified file is not downloaded again");
    }

    [Fact]
    public async Task DownloadScripts_ADiskError_LeavesTheScriptOut()
    {
        var document = DocumentWithTexts();
        var h = Create(_ => Json(ListedDescriptor(document)), byRequest: r => Bytes(Store(document)(r)));
        var loaded = await h.Loader.LoadAsync(SessionId);
        var volume = new FakeSetupFileSystem { FailWrites = true };

        var files = await h.Loader.DownloadScriptsAsync(SessionId, loaded, volume);

        files.Should().BeEmpty();
        h.Logger.Messages.Should().Contain(m => m.Contains("could not be written to the Windows volume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadScripts_AnotherVersion_DownloadsNothing()
    {
        var document = DocumentWithTexts();
        var h = Create(call => Json(call == 1 ? ListedDescriptor(document) : Descriptor(document, sha: new string('0', 64), scripts: [Text(Everywhere, EverywhereText, "everywhere")])), byRequest: r => Bytes(Store(document)(r)));
        var loaded = await h.Loader.LoadAsync(SessionId);
        var before = h.Download.Requests.Count;

        var files = await h.Loader.DownloadScriptsAsync(SessionId, loaded, new FakeSetupFileSystem());

        files.Should().BeEmpty();
        h.Download.Requests.Should().HaveCount(before);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task DownloadScripts_WithoutADescriptor_DownloadsNothing(HttpStatusCode status)
    {
        var document = DocumentWithTexts();
        var h = Create(call => call == 1 ? Json(ListedDescriptor(document)) : new HttpResponseMessage(status), byRequest: r => Bytes(Store(document)(r)));
        var loaded = await h.Loader.LoadAsync(SessionId);

        var files = await h.Loader.DownloadScriptsAsync(SessionId, loaded, new FakeSetupFileSystem());

        files.Should().BeEmpty();
    }

    [Fact]
    public async Task DownloadScripts_WithoutScripts_CallsNothing()
    {
        var document = Document();
        var h = Create(_ => Json(Descriptor(document)), _ => Bytes(document));
        var loaded = await h.Loader.LoadAsync(SessionId);

        var files = await h.Loader.DownloadScriptsAsync(SessionId, loaded, new FakeSetupFileSystem());

        files.Should().BeEmpty();
        h.Gateway.Requests.Should().HaveCount(1);
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
