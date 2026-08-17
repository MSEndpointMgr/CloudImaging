using System.Net;
using CloudImaging.DeviceGatewayApi.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>
/// Behavioral tests for the boot-image self-update forwarding calls added to
/// <see cref="ImagingCoreClient"/> (T071b, FR-059a) — verified against a fake
/// <see cref="HttpMessageHandler"/> rather than asserting on unrelated local constants.
/// </summary>
public sealed class ImagingCoreClientBootImageTests
{
    [Fact]
    public async Task GetBootImagesAsync_IssuesGetRequest_ToInternalBootImagesCatalogEndpoint()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var client = new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.example.com") });

        var response = await client.GetBootImagesAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/internal/boot-images");
    }

    [Fact]
    public async Task GetBootImageSasUrlAsync_IssuesPostRequest_ToBootImageSasEndpoint_ForTheGivenId()
    {
        var bootImageId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"sasTokenUrl":"https://sas.example.com/x","sha256Hash":"abc"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var client = new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.example.com") });

        var response = await client.GetBootImageSasUrlAsync(bootImageId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be($"/api/internal/boot-images/{bootImageId}/sas");
    }

    [Fact]
    public async Task GetBootImagesAsync_PropagatesNonSuccessStatusCode_WithoutThrowing()
    {
        // GetLatestBootImageFunction relies on IsSuccessStatusCode (not EnsureSuccessStatusCode)
        // so it can translate a downstream failure into its own 503 response.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = new ImagingCoreClient(new HttpClient(handler) { BaseAddress = new Uri("https://core.example.com") });

        var response = await client.GetBootImagesAsync();

        response.IsSuccessStatusCode.Should().BeFalse();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
