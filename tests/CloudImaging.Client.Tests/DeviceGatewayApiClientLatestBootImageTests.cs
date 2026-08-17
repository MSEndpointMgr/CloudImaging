using CloudImaging.Client.Services;
using FluentAssertions;
using System.Net;
using System.Net.Http;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Unit tests for <see cref="DeviceGatewayApiClient.GetLatestBootImageAsync"/> (T071b, FR-059a).
/// </summary>
public sealed class DeviceGatewayApiClientLatestBootImageTests
{
    [Fact]
    public async Task GetLatestBootImageAsync_ReturnsParsedInfo_OnSuccessResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"version":"2.5.0","sha256Hash":"deadbeef","sasTokenUrl":"https://sas.example.com/x"}""",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });

        var result = await client.GetLatestBootImageAsync();

        result.Should().NotBeNull();
        result!.Version.Should().Be("2.5.0");
        result.Sha256Hash.Should().Be("deadbeef");
        result.SasTokenUrl.Should().Be("https://sas.example.com/x");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetLatestBootImageAsync_ReturnsNull_OnNonSuccessResponse(HttpStatusCode statusCode)
    {
        // Must return null (never throw) on any non-success response — the self-update check
        // treats network/service failures as "nothing to update" rather than a hard error.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(statusCode));
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });

        var result = await client.GetLatestBootImageAsync();

        result.Should().BeNull();
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
