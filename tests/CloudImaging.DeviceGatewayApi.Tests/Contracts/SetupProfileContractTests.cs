using System.Net;
using System.Text;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Functions;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Services;
using CloudImaging.DeviceGatewayApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

public sealed class SetupProfileContractTests
{
    private static readonly Guid SessionId = Guid.NewGuid();

    private const string CoreDescriptor = """
        {
          "profileId": "8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10",
          "profileName": "Stockholm and Gothenburg",
          "version": 8,
          "failureHandling": "CompleteWithWarnings",
          "contentUrl": "https://corestorage.blob.core.windows.net/setup-profiles/8f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10/000008.json?sv=2024&sig=secret",
          "contentUrlExpiresAt": "2026-10-23T10:15:00Z",
          "contentSha256": "3b1fe9",
          "contentSizeBytes": 18234,
          "internalOnly": "must not reach devices"
        }
        """;

    private static (SessionSetupProfileFunction Function, CapturingHandler Handler, ListLogger Logger) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new CapturingHandler(respond);
        var logger = new ListLogger();
        var client = new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.internal") });
        return (new SessionSetupProfileFunction(client, logger), handler, logger);
    }

    private static Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> RunAsync(SessionSetupProfileFunction function, string routeId, Guid? tokenSession = null)
    {
        var context = FunctionHttp.Context(new Dictionary<object, object> { [DeviceSessionTokenValidationMiddleware.SessionIdKey] = tokenSession ?? SessionId });
        return function.Run(FunctionHttp.Request(context, "GET", $"https://gateway/api/v1/sessions/{routeId}/setup-profile"), routeId, context);
    }

    [Fact]
    public async Task Ok_RelaysExactlyTheDescriptorFields()
    {
        var (function, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CoreDescriptor, Encoding.UTF8, "application/json") });

        var response = await RunAsync(function, SessionId.ToString());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be($"/api/internal/sessions/{SessionId}/setup-profile");
        using var json = JsonDocument.Parse(await response.ReadBodyAsync());
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["profileId", "profileName", "version", "failureHandling", "contentUrl", "contentUrlExpiresAt", "contentSha256", "contentSizeBytes"]);
        json.RootElement.GetProperty("version").GetInt32().Should().Be(8);
    }

    [Fact]
    public async Task Ok_NeverLogsTheReadUrl_OnlyItsHost()
    {
        var (function, _, logger) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(CoreDescriptor, Encoding.UTF8, "application/json") });

        await RunAsync(function, SessionId.ToString());

        logger.Messages.Should().NotContain(m => m.Contains("sig=", StringComparison.Ordinal) || m.Contains("000008.json", StringComparison.Ordinal));
        logger.Messages.Should().Contain(m => m.Contains("corestorage.blob.core.windows.net", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent, HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict, HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable)]
    public async Task CoreStatus_IsRelayedOrMappedTo503(HttpStatusCode core, HttpStatusCode expected)
    {
        var (function, _, _) = Create(_ => new HttpResponseMessage(core));

        (await RunAsync(function, SessionId.ToString())).StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task CoreUnreachable_Returns503()
    {
        var (function, _, _) = Create(_ => throw new HttpRequestException("connection refused"));

        (await RunAsync(function, SessionId.ToString())).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task RouteSessionDifferentFromToken_Returns403_WithoutCallingCore()
    {
        var (function, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));

        (await RunAsync(function, SessionId.ToString(), tokenSession: Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NonGuidId_Returns400()
    {
        var (function, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));

        (await RunAsync(function, "not-a-guid")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Function_IsNotExemptFromTheSessionToken()
    {
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().NotContain("GetSessionSetupProfile");
    }

    [Fact]
    public async Task ProgressRelay_KeepsWarningCountAndItemsIntact()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.internal") });
        var function = new ReportProgressFunction(client, NullLogger<ReportProgressFunction>.Instance);
        const string body = """{"stepName":"PrepareSetup","status":"Completed","warningCount":1,"items":[{"index":0,"kind":"WinPEScript","name":"Enable Wake on LAN","outcome":"Failed","reason":"Exited with code 1.","exitCode":1,"durationMs":8123,"lastOutputLine":"BCU returned 13"}]}""";
        var context = FunctionHttp.Context();

        var response = await function.Run(FunctionHttp.Request(context, "POST", "https://gateway/x", body), SessionId.ToString(), context);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var sent = JsonDocument.Parse(handler.Requests.Single().Body!);
        using var expected = JsonDocument.Parse(body);
        sent.RootElement.GetProperty("warningCount").GetInt32().Should().Be(1);
        sent.RootElement.GetProperty("items").GetRawText().Should().Be(expected.RootElement.GetProperty("items").GetRawText());
    }

    private sealed class ListLogger : ILogger<SessionSetupProfileFunction>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
