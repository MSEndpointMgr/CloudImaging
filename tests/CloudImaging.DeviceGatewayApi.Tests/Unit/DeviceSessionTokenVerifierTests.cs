using System.Net;
using CloudImaging.DeviceGatewayApi.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.DeviceGatewayApi.Tests.Unit;

/// <summary>A device-session token is trusted only when Imaging Core confirms its hash.</summary>
public sealed class DeviceSessionTokenVerifierTests
{
    private static readonly Guid SessionId = Guid.Parse("6f0c3c5e-6c1f-4c55-9a43-2f5d8c1e7a10");

    private static (DeviceSessionTokenVerifier Verifier, List<(Guid Session, string Hash)> Calls) Verifier(params HttpStatusCode[] answers)
    {
        var calls = new List<(Guid, string)>();
        var queue = new Queue<HttpStatusCode>(answers);
        var verifier = new DeviceSessionTokenVerifier((session, hash, _) =>
        {
            calls.Add((session, hash));
            return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : HttpStatusCode.NoContent);
        }, NullLogger.Instance);
        return (verifier, calls);
    }

    [Fact]
    public async Task CheckAsync_ConfirmedToken_IsValidForItsSession_AndAsksCoreWithItsHashOnly()
    {
        var token = new DeviceSessionTokenService().IssueToken(SessionId);
        var (verifier, calls) = Verifier(HttpStatusCode.NoContent);

        var (check, session) = await verifier.CheckAsync(token, CancellationToken.None);

        check.Should().Be(DeviceTokenCheck.Valid);
        session.Should().Be(SessionId);
        calls.Should().ContainSingle().Which.Should().Be((SessionId, DeviceSessionTokenService.Hash(token)));
    }

    [Fact]
    public async Task CheckAsync_TokenCoreDoesNotKnow_IsInvalid_AndIsAskedAgainNextTime()
    {
        var token = new DeviceSessionTokenService().IssueToken(SessionId);
        var (verifier, calls) = Verifier(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);

        (await verifier.CheckAsync(token, CancellationToken.None)).Check.Should().Be(DeviceTokenCheck.Invalid);
        (await verifier.CheckAsync(token, CancellationToken.None)).Check.Should().Be(DeviceTokenCheck.Invalid);

        calls.Should().HaveCount(2, "a refusal is never cached");
    }

    [Fact]
    public async Task CheckAsync_GuessedSuffixForAKnownSession_IsInvalid()
    {
        var forged = Convert.ToBase64String([.. SessionId.ToByteArray(), .. new byte[32]]).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var (verifier, _) = Verifier(HttpStatusCode.Unauthorized);

        (await verifier.CheckAsync(forged, CancellationToken.None)).Check.Should().Be(DeviceTokenCheck.Invalid);
    }

    [Fact]
    public async Task CheckAsync_CoreUnreachable_IsUnavailable_NeverValid()
    {
        var token = new DeviceSessionTokenService().IssueToken(SessionId);
        var verifier = new DeviceSessionTokenVerifier((_, _, _) => throw new HttpRequestException("down"), NullLogger.Instance);

        (await verifier.CheckAsync(token, CancellationToken.None)).Check.Should().Be(DeviceTokenCheck.Unavailable);
    }

    [Fact]
    public async Task CheckAsync_ConfirmedToken_IsCachedForAMinute()
    {
        var token = new DeviceSessionTokenService().IssueToken(SessionId);
        var (verifier, calls) = Verifier(HttpStatusCode.NoContent);

        await verifier.CheckAsync(token, CancellationToken.None);
        await verifier.CheckAsync(token, CancellationToken.None);

        calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task CheckAsync_MalformedToken_IsInvalid_WithoutAskingCore()
    {
        var (verifier, calls) = Verifier();

        (await verifier.CheckAsync("abc", CancellationToken.None)).Check.Should().Be(DeviceTokenCheck.Invalid);
        calls.Should().BeEmpty();
    }
}
