using System.Net;
using System.Text;
using CloudImaging.Contracts.Models.SetupProfiles;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Services;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using static CloudImaging.ImagingCoreApi.Tests.TestDoubles.SetupProfileTestBed;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>Uploading a script or answer file text, and reading one back for its profile.</summary>
public sealed class SetupProfileContentFunctionsTests
{
    private const string ValidAnswerFile = "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\"><settings pass=\"specialize\"/><settings pass=\"oobeSystem\"/></unattend>";
    private readonly SetupProfileTestBed _bed = new();
    private readonly SetupProfileContentFunctions _functions;

    public SetupProfileContentFunctionsTests() => _functions = new SetupProfileContentFunctions(_bed.Service);

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> UploadAsync(string? kind, byte[] body, long? declaredLength = null)
    {
        var request = FunctionTestHost.RawRequest("POST", $"https://core/api/internal/setup-profiles/content{(kind is null ? string.Empty : $"?kind={kind}")}", body);
        if (declaredLength is { } length)
        {
            request.Headers.Add("Content-Length", length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return _functions.Upload(request, FunctionTestHost.Context);
    }

    [Fact]
    public async Task Upload_StoresTheTextOnce_AndReturnsHashAndSize()
    {
        var response = await UploadAsync("script", Encoding.UTF8.GetBytes("exit 0"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.ReadJsonAsync();
        json.GetProperty("sha256").GetString().Should().Be(Sha("exit 0"));
        json.GetProperty("sizeBytes").GetInt64().Should().Be(6);
        json.TryGetProperty("problems", out _).Should().BeFalse();

        (await UploadAsync("script", Encoding.UTF8.GetBytes("exit 0"))).StatusCode.Should().Be(HttpStatusCode.OK);
        _bed.Blobs.Blobs.Keys.Should().Equal($"content/{Sha("exit 0")}");
        _bed.Blobs.Metadata[$"content/{Sha("exit 0")}"].Should().ContainKeys("uploadedAt", "kind");
    }

    [Fact]
    public async Task Upload_ALeadingBom_IsNotStoredHashedOrCounted()
    {
        var json = await (await UploadAsync("script", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("exit 0")])).ReadJsonAsync();

        json.GetProperty("sha256").GetString().Should().Be(Sha("exit 0"));
        json.GetProperty("sizeBytes").GetInt64().Should().Be(6);
        _bed.Blobs.Blobs.Single().Value.ToString().Should().Be("exit 0");
    }

    [Fact]
    public async Task Upload_AScriptOf24MiB_IsAccepted_AndOneByteMoreIs413WithItsSize()
    {
        var exactly = new byte[SetupProfileContent.MaxScriptBytes];
        Array.Fill(exactly, (byte)'x');
        (await UploadAsync("script", [0xEF, 0xBB, 0xBF, .. exactly])).StatusCode.Should().Be(HttpStatusCode.OK);

        var over = await UploadAsync("script", [.. exactly, (byte)'x']);
        over.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await over.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("This script is 24.1 MB. A script can be at most 24 MB.");
    }

    [Fact]
    public async Task Upload_AScriptOf24MiB_WithItsContentLength_IsAccepted()
    {
        var exactly = new byte[SetupProfileContent.MaxScriptBytes];
        Array.Fill(exactly, (byte)'y');

        var json = await (await UploadAsync("script", exactly, declaredLength: exactly.Length)).ReadJsonAsync();

        json.GetProperty("sizeBytes").GetInt64().Should().Be(SetupProfileContent.MaxScriptBytes);
    }

    [Fact]
    public async Task Upload_ABodyLongerThanItsContentLength_IsStillReadWhole()
    {
        var text = new string('z', 200_000);

        var json = await (await UploadAsync("script", Encoding.UTF8.GetBytes(text), declaredLength: 6)).ReadJsonAsync();

        json.GetProperty("sha256").GetString().Should().Be(Sha(text));
        json.GetProperty("sizeBytes").GetInt64().Should().Be(200_000);
    }

    [Fact]
    public async Task Upload_LogsTheUploadingUser()
    {
        var logger = new ListLogger();
        var service = new SetupProfileService(_bed.Profiles, _bed.Targets, _bed.Versions, _bed.Texts, _bed.History, _bed.Locations, _bed.Sessions, _bed.Config, _bed.Time, logger);
        var request = FunctionTestHost.RawRequest("POST", "https://core/api/internal/setup-profiles/content?kind=script&uploadedBy=olle%40contoso.com", Encoding.UTF8.GetBytes("exit 0"));

        (await new SetupProfileContentFunctions(service).Upload(request, FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.OK);

        logger.Messages.Should().ContainSingle(m => m.Contains("uploaded by olle@contoso.com", StringComparison.Ordinal) && m.Contains(Sha("exit 0"), StringComparison.Ordinal));
    }

    private sealed class ListLogger : ILogger<SetupProfileService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task Upload_TooLarge_UsesTheDeclaredLengthForTheSize()
    {
        var body = new byte[SetupProfileContent.MaxScriptBytes + 10];

        var response = await UploadAsync("script", body, declaredLength: 26_528_972);

        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("This script is 25.3 MB. A script can be at most 24 MB.");
    }

    [Fact]
    public async Task Upload_AnAnswerFileOver2MiB_Is413WithTheAnswerFileMessage()
    {
        var response = await UploadAsync("answerFile", new byte[SetupProfileContent.MaxAnswerFileBytes + 1]);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("The answer file is larger than 2 MB. Remove settings you do not need.");
        _bed.Blobs.Blobs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("driver")]
    [InlineData("Script")]
    public async Task Upload_AnUnknownKind_Is400(string? kind)
    {
        (await UploadAsync(kind, Encoding.UTF8.GetBytes("exit 0"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _bed.Blobs.Blobs.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    [InlineData("\uFEFF")]
    public async Task Upload_AnEmptyText_Is400(string text)
    {
        var response = await UploadAsync("script", Encoding.UTF8.GetBytes(text));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.ReadJsonAsync()).GetProperty("detail").GetString().Should().Be("The script is empty. Add the script text.");
    }

    [Fact]
    public async Task Upload_AnAnswerFile_ReturnsPassesAndNoProblems()
    {
        var json = await (await UploadAsync("answerFile", Encoding.UTF8.GetBytes(ValidAnswerFile))).ReadJsonAsync();

        json.GetProperty("passes").EnumerateArray().Select(p => p.GetString()).Should().Equal("specialize", "oobeSystem");
        json.GetProperty("problems").GetArrayLength().Should().Be(0);
        _bed.Blobs.Metadata.Single().Value["kind"].Should().Be("answerFile");
    }

    [Fact]
    public async Task Upload_AnAnswerFileWithProblems_IsStored_AndReturnsTheProblems()
    {
        const string bad = "<unattend xmlns=\"urn:schemas-microsoft-com:unattend\">\n<settings pass=\"windowsPE\"/>\n</unattend>";

        var response = await UploadAsync("answerFile", Encoding.UTF8.GetBytes(bad));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var problem = (await response.ReadJsonAsync()).GetProperty("problems")[0];
        problem.GetProperty("code").GetString().Should().Be("windowsPePass");
        problem.GetProperty("line").GetInt32().Should().Be(2);
        _bed.Blobs.Blobs.Should().ContainSingle();
    }

    [Fact]
    public async Task Read_ReturnsTheTextOfAScriptTheProfileUses_Else404()
    {
        var detail = await _bed.CreateAsync("Stockholm", At(_bed.Stockholm), Content() with { Scripts = [_bed.Script("Wake", "Write-Output 'H\u00e4j'")] });
        var other = _bed.Upload("Write-Output other");

        var ok = await _functions.Read(FunctionTestHost.Request("GET", "https://core/x"), detail.ProfileId.ToString(), Sha("Write-Output 'H\u00e4j'"), FunctionTestHost.Context);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Headers.GetValues("Content-Type").Single().Should().Be("text/plain; charset=utf-8");
        (await ok.ReadBodyAsync()).Should().Be("Write-Output 'H\u00e4j'");

        (await _functions.Read(FunctionTestHost.Request("GET", "https://core/x"), detail.ProfileId.ToString(), other, FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _functions.Read(FunctionTestHost.Request("GET", "https://core/x"), "x", other, FunctionTestHost.Context)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
