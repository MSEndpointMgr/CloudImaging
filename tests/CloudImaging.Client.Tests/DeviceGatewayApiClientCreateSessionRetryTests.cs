using CloudImaging.Client.Services;
using CloudImaging.Contracts.Models;
using FluentAssertions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Session creation must survive the refused/reset TCP connect a freshly booted device hits before
/// its network path is usable, instead of surfacing it as an error the operator has to click past.
/// </summary>
public sealed class DeviceGatewayApiClientCreateSessionRetryTests
{
    private static readonly DeviceRegistrationPayload Payload = new()
    {
        SerialNumber = "SN-1",
        Manufacturer = "Contoso",
        Model = "Model",
    };

    private const string SuccessBody =
        """{"sessionId":"8bcf0c9e-6e6e-4f3a-9a4c-6d5c2f4b1a21","deviceSessionToken":"t","passcode":"4CL3P5","state":"SessionAllowed"}""";

    private static HttpRequestException ConnectionRefused() =>
        new(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it. (gw.example.com:443)",
            new SocketException((int)SocketError.ConnectionRefused));

    [Fact]
    public async Task CreateSessionAsync_RecoversFromATransientConnectionRefusal()
    {
        var handler = new ScriptedHandler(attempt => attempt == 1
            ? throw ConnectionRefused()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessBody, System.Text.Encoding.UTF8, "application/json"),
            });
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });

        var result = await client.CreateSessionAsync(Payload);

        result.Should().NotBeNull();
        handler.Attempts.Should().Be(2, "the first connect failed and the second succeeded");
    }

    [Fact]
    public async Task CreateSessionAsync_GivesUpAfterThreeConnectionFailures()
    {
        var handler = new ScriptedHandler(_ => throw ConnectionRefused());
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });

        var act = async () => await client.CreateSessionAsync(Payload);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Attempts.Should().Be(3, "a persistent outage must surface rather than retry forever");
    }

    [Fact]
    public async Task CreateSessionAsync_DoesNotRetryAGatewayRejection()
    {
        // A refused certificate or proof-of-possession will be refused every time, so replaying it
        // only delays the error the operator needs to see.
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("certificate thumbprint mismatch"),
        });
        var client = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });

        var act = async () => await client.CreateSessionAsync(Payload);

        await act.Should().ThrowAsync<DeviceGatewayApiException>();
        handler.Attempts.Should().Be(1);
    }

    private sealed class ScriptedHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(respond(Attempts));
        }
    }
}
