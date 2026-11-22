using System.Net;
using System.Net.Http;
using CloudImaging.Client.Services;
using CloudImaging.Client.Tests.TestDoubles;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Only a 404 means no recovery image is published. A rate limit or an outage must not pass for
/// that, or the OS image's own WinRE silently replaces the published one.
/// </summary>
public sealed class DeviceGatewayApiClientRecoveryImageTests
{
    private const string Published = """{"version":"1.2.0","sha256Hash":"abc123","sasTokenUrl":"https://sas.example.com/winre.wim?sig=x"}""";

    [Fact]
    public async Task NothingPublished_IsNull_WithoutAskingAgain()
    {
        var (handler, client, waits) = Scripted(new HttpResponseMessage(HttpStatusCode.NotFound));

        (await client.GetLatestRecoveryImageAsync()).Should().BeNull();

        handler.Requests.Should().ContainSingle();
        waits.Should().BeEmpty();
    }

    [Fact]
    public async Task RateLimited_IsAskedAgainAfterTheRetryAfter()
    {
        var (handler, client, waits) = Scripted(ImagingProgressReporterTests.RateLimited(4), new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Published, System.Text.Encoding.UTF8, "application/json"),
        });

        var image = await client.GetLatestRecoveryImageAsync();

        image!.Version.Should().Be("1.2.0");
        handler.Requests.Should().HaveCount(2);
        waits.Should().Equal(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task UnavailableCatalog_Throws_AfterThreeAttempts()
    {
        var (handler, client, _) = Scripted(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var act = () => client.GetLatestRecoveryImageAsync();

        (await act.Should().ThrowAsync<DeviceGatewayApiException>()).Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Rejection_Throws_WithoutAskingAgain()
    {
        var (handler, client, _) = Scripted(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var act = () => client.GetLatestRecoveryImageAsync();

        await act.Should().ThrowAsync<DeviceGatewayApiException>();
        handler.Requests.Should().ContainSingle("a refused token is refused every time");
    }

    private static (RecordingHandler Handler, DeviceGatewayApiClient Client, List<TimeSpan> Waits) Scripted(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        var handler = new RecordingHandler(_ => queue.Dequeue());
        var waits = new List<TimeSpan>();
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }, delay: (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });
        return (handler, client, waits);
    }
}
