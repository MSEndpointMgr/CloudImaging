using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using CloudImaging.Client.Logging;
using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>Expected network failures are logged as one line; anything else keeps its stack trace.</summary>
public sealed class LoggedFailureTests
{
    public static TheoryData<Exception> NetworkFailures => new()
    {
        new HttpRequestException("No such host is known. (gw.example.com:443)", new SocketException((int)SocketError.HostNotFound)),
        new DeviceGatewayApiException(HttpStatusCode.ServiceUnavailable, null, null),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."),
        new SocketException((int)SocketError.TimedOut),
    };

    [Theory]
    [MemberData(nameof(NetworkFailures))]
    public void NetworkFailures_AreLoggedWithoutAStackTrace(Exception ex)
    {
        LoggedFailure.StackTrace(ex).Should().BeNull();
    }

    [Fact]
    public void UnexpectedFailures_KeepTheirStackTrace()
    {
        var ex = new InvalidOperationException("Manifest is corrupt.");

        LoggedFailure.StackTrace(ex).Should().BeSameAs(ex);
    }

    [Fact]
    public void Reason_SkipsAnInnerMessageAlreadyInTheOuterOne()
    {
        var ex = new HttpRequestException("No such host is known. (gw.example.com:443)", new SocketException((int)SocketError.HostNotFound));

        LoggedFailure.Reason(ex).Should().Be(ex.Message);
    }

    [Fact]
    public void Reason_AppendsAnInnerMessageThatAddsDetail()
    {
        var ex = new HttpRequestException("An error occurred while sending the request.", new IOException("Connection reset by peer."));

        LoggedFailure.Reason(ex).Should().Be("An error occurred while sending the request. (Connection reset by peer.)");
    }
}
