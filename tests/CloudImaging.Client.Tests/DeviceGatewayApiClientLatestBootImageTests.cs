using CloudImaging.Client.Services;
using CloudImaging.Contracts.Models;
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

    [Theory]
    [InlineData(MachineArchitecture.X64, "x64")]
    [InlineData(MachineArchitecture.Arm64, "arm64")]
    public async Task GetLatestBootImageAsync_RequestsLatestForItsOwnArchitecture(MachineArchitecture architecture, string expectedQuery)
    {
        Uri? requested = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });

        await client.GetLatestBootImageAsync(architecture);

        requested!.PathAndQuery.Should().Be($"/api/v1/boot-image/latest?architecture={expectedQuery}",
            "latest is resolved per architecture, so an ARM64 stick must never be offered the x64 latest image");
    }

    [Fact]
    public async Task GetLatestBootImageAsync_SendsCurrentImageId_AndTreats204AsNoUpdate()
    {
        Uri? requested = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });
        var current = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var result = await client.GetLatestBootImageAsync(MachineArchitecture.X64, current);

        requested!.Query.Should().Contain($"currentBootImageId={current}");
        result.Should().BeNull("204 means this stick runs a pre-production image that must not be replaced");
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
