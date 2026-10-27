using System.Net;
using System.Text;
using System.Text.Json;
using CloudImaging.DeviceGatewayApi.Functions;
using CloudImaging.DeviceGatewayApi.Middleware;
using CloudImaging.DeviceGatewayApi.Services;
using CloudImaging.DeviceGatewayApi.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Contracts;

public sealed class DriverSetContractTests
{
    private static readonly Guid SessionId = Guid.NewGuid();

    private const string CoreDescriptor = """
        {
          "skipReason": null,
          "matchKey": "8C26",
          "modelName": "HP EliteBook 840 G11",
          "release": "Windows11_24H2",
          "setVersion": 7,
          "internalOnly": "must not reach devices",
          "components": [
            {
              "index": 0, "name": "Intel Wireless LAN Driver", "componentId": "sp150123", "version": "23.80.0.4",
              "origin": "Vendor", "category": "Driver - Network", "action": "Offline", "critical": true,
              "sizeBytes": 113000000, "sha256": "ab12", "fileName": "sp150123.exe",
              "downloadUrl": "https://corestorage.blob.core.windows.net/drivers/files/Hp/sp150123/23.80.0.4/sp150123.exe?sv=2024&sig=secret",
              "downloadUrlExpiresAt": "2026-10-24T10:15:00Z", "extract": "Hp",
              "installCommand": { "executable": "HPUP.exe", "arguments": "/s", "internalOnly": "x" },
              "pciIds": [ { "vendor": "8086", "device": "51F0", "subVendor": "1028", "subDevice": "0CB4", "internalOnly": "x" } ],
              "vendorData": "must not reach devices"
            }
          ]
        }
        """;

    private static (SessionDriverSetFunction Function, CapturingHandler Handler, ListLogger Logger) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new CapturingHandler(respond);
        var logger = new ListLogger();
        var client = new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.internal") });
        return (new SessionDriverSetFunction(client, logger), handler, logger);
    }

    private static Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> GetAsync(SessionDriverSetFunction function, string routeId, Guid? tokenSession = null)
    {
        var context = FunctionHttp.Context(new Dictionary<object, object> { [DeviceSessionTokenValidationMiddleware.SessionIdKey] = tokenSession ?? SessionId });
        return function.GetDriverSet(FunctionHttp.Request(context, "GET", $"https://gateway/api/v1/sessions/{routeId}/driver-set"), routeId, context);
    }

    private static Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> LinkAsync(SessionDriverSetFunction function, string index, Guid? tokenSession = null)
    {
        var context = FunctionHttp.Context(new Dictionary<object, object> { [DeviceSessionTokenValidationMiddleware.SessionIdKey] = tokenSession ?? SessionId });
        return function.RefreshLink(FunctionHttp.Request(context, "POST", $"https://gateway/api/v1/sessions/{SessionId}/driver-set/components/{index}/link"), SessionId.ToString(), index, context);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Ok_RelaysExactlyTheDescriptorFields()
    {
        var (function, handler, _) = Create(_ => Json(CoreDescriptor));

        var response = await GetAsync(function, SessionId.ToString());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be($"/api/internal/sessions/{SessionId}/driver-set");
        using var json = JsonDocument.Parse(await response.ReadBodyAsync());
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["skipReason", "matchKey", "modelName", "release", "setVersion", "components"]);
        var component = json.RootElement.GetProperty("components").EnumerateArray().Single();
        component.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["index", "name", "componentId", "version", "origin", "category", "action", "critical", "sizeBytes", "sha256", "fileName", "downloadUrl", "downloadUrlExpiresAt", "extract", "installCommand", "pciIds"]);
        component.GetProperty("critical").GetBoolean().Should().BeTrue();
        component.GetProperty("installCommand").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["executable", "arguments"]);
        component.GetProperty("pciIds")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["vendor", "device", "subVendor", "subDevice"]);
        json.RootElement.GetProperty("setVersion").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task SkipReason_IsRelayedWithTheMatchKeyAndNoComponents()
    {
        var (function, _, _) = Create(_ => Json("""{"skipReason":"NoMatchingModel","matchKey":"8A3B","modelName":null,"release":"Windows11_24H2","setVersion":null,"components":[]}"""));

        using var json = JsonDocument.Parse(await (await GetAsync(function, SessionId.ToString())).ReadBodyAsync());

        json.RootElement.GetProperty("skipReason").GetString().Should().Be("NoMatchingModel");
        json.RootElement.GetProperty("matchKey").GetString().Should().Be("8A3B");
        json.RootElement.GetProperty("components").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Ok_LogsOnlyTheHostOfTheReadLinks()
    {
        var (function, _, logger) = Create(_ => Json(CoreDescriptor));

        await GetAsync(function, SessionId.ToString());

        logger.Messages.Should().NotContain(m => m.Contains("sig=", StringComparison.Ordinal) || m.Contains("sp150123.exe", StringComparison.Ordinal));
        logger.Messages.Should().Contain(m => m.Contains("corestorage.blob.core.windows.net", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict, HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    public async Task CoreStatus_IsRelayedOrMappedTo503(HttpStatusCode core, HttpStatusCode expected)
    {
        var (function, _, _) = Create(_ => new HttpResponseMessage(core));

        (await GetAsync(function, SessionId.ToString())).StatusCode.Should().Be(expected);
    }

    [Fact]
    public async Task RateLimited_RelaysRetryAfter()
    {
        var (function, _, _) = Create(_ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return limited;
        });

        var response = await GetAsync(function, SessionId.ToString());

        response.Headers.GetValues("Retry-After").Single().Should().Be("12");
    }

    [Fact]
    public async Task CoreUnreachable_Returns503()
    {
        var (function, _, _) = Create(_ => throw new HttpRequestException("connection refused"));

        (await GetAsync(function, SessionId.ToString())).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task RouteSessionDifferentFromToken_Returns403_WithoutCallingCore()
    {
        var (function, handler, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK));

        (await GetAsync(function, SessionId.ToString(), tokenSession: Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await LinkAsync(function, "0", tokenSession: Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Link_RelaysOnlyTheUrlAndExpiry_And400ForABadIndex()
    {
        var (function, handler, _) = Create(_ => Json("""{"downloadUrl":"https://corestorage.blob.core.windows.net/drivers/x?sig=secret","downloadUrlExpiresAt":"2026-10-24T11:15:00Z","extra":"x"}"""));

        var response = await LinkAsync(function, "3");
        var bad = await LinkAsync(function, "-1");

        handler.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be($"/api/internal/sessions/{SessionId}/driver-set/components/3/link");
        using var json = JsonDocument.Parse(await response.ReadBodyAsync());
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["downloadUrl", "downloadUrlExpiresAt"]);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Functions_AreNotExemptFromTheSessionToken()
    {
        DeviceSessionTokenValidationMiddleware.ExemptFunctionNames.Should().NotContain(["GetSessionDriverSet", "RefreshSessionDriverComponentLink"]);
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<SessionDriverSetFunction>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
