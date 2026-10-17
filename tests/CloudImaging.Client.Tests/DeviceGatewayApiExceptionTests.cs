using System.Net;
using System.Net.Http;
using System.Text;
using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>Gateway failures become exception messages shown on screen, so only short, readable reasons may pass through.</summary>
public sealed class DeviceGatewayApiExceptionTests
{
    private static Task<DeviceGatewayApiException> FromAsync(HttpStatusCode status, string body, string mediaType) =>
        DeviceGatewayApiException.FromResponseAsync(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) }, CancellationToken.None);

    [Fact]
    public async Task ProblemDetails_UsesTheDetailAndType()
    {
        var ex = await FromAsync(HttpStatusCode.Unauthorized, """{"type":"https://cloudimaging.io/errors/unauthorized","detail":"Token expired."}""", "application/problem+json");

        ex.Message.Should().Be("Token expired.");
        ex.ProblemType.Should().Be(DeviceGatewayApiException.TokenProblemType);
    }

    [Fact]
    public async Task ShortPlainTextReason_IsKept()
    {
        var ex = await FromAsync(HttpStatusCode.Forbidden, "Client certificate thumbprint mismatch.", "text/plain");

        ex.Message.Should().Be("Client certificate thumbprint mismatch.");
    }

    [Theory]
    [InlineData("<!DOCTYPE html>\n<html><head><title>Web App - Unavailable</title></head></html>", "text/html")]
    [InlineData("<html><body>Service Unavailable</body></html>", "text/plain")]
    [InlineData("line one\nline two", "text/plain")]
    public async Task HtmlOrMultiLineBody_FallsBackToTheStatusCode(string body, string mediaType)
    {
        var ex = await FromAsync(HttpStatusCode.ServiceUnavailable, body, mediaType);

        ex.Message.Should().Be("Device Gateway API returned HTTP 503.");
    }

    [Fact]
    public async Task OverlongPlainTextBody_FallsBackToTheStatusCode()
    {
        var ex = await FromAsync(HttpStatusCode.BadGateway, new string('x', 301), "text/plain");

        ex.Message.Should().Be("Device Gateway API returned HTTP 502.");
    }
}
