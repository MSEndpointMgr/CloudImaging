using System.Net;
using CloudImaging.ImagingCoreApi.Functions;
using CloudImaging.ImagingCoreApi.Repositories;
using CloudImaging.ImagingCoreApi.Tests.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Functions;

/// <summary>Imaging Core keeps the device-session token hash and is the only judge of a token.</summary>
public sealed class SessionDeviceTokenFunctionsTests
{
    private readonly DeviceSessionRepository _sessions;
    private readonly SessionDeviceTokenFunctions _functions;
    private readonly Guid _sessionId = Guid.NewGuid();
    private readonly string _hash = new('a', 64);

    public SessionDeviceTokenFunctionsTests()
    {
        var (tables, _) = InMemoryTableClient.CreateService();
        _sessions = new DeviceSessionRepository(tables);
        _sessions.CreateAsync(Services.Drivers.DriverSetResolverTests.Device("8C26") with { SessionId = _sessionId }).GetAwaiter().GetResult();
        _functions = new SessionDeviceTokenFunctions(_sessions, TimeProvider.System, NullLogger<SessionDeviceTokenFunctions>.Instance);
    }

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> SetAsync(object body, Guid? session = null) =>
        _functions.SetToken(FunctionTestHost.Request("PUT", $"https://core/api/internal/sessions/{session ?? _sessionId}/device-token", body), (session ?? _sessionId).ToString(), FunctionTestHost.Context);

    private Task<Microsoft.Azure.Functions.Worker.Http.HttpResponseData> VerifyAsync(string hash) =>
        _functions.VerifyToken(FunctionTestHost.Request("POST", $"https://core/api/internal/sessions/{_sessionId}/device-token/verify", new { tokenHash = hash }), _sessionId.ToString(), FunctionTestHost.Context);

    [Fact]
    public async Task Verify_StoredUnexpiredHash_Is204_AndAnyOtherIs401()
    {
        (await SetAsync(new { tokenHash = _hash, expiresAt = DateTimeOffset.UtcNow.AddHours(24) })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await VerifyAsync(_hash)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await VerifyAsync(new string('b', 64))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _sessions.GetByIdAsync(_sessionId))!.DeviceSessionTokenHash.Should().Be(_hash);
    }

    [Fact]
    public async Task Verify_SessionWithoutAStoredToken_Is401()
    {
        (await VerifyAsync(_hash)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Verify_ExpiredToken_Is401()
    {
        await _sessions.SetDeviceTokenAsync(_sessionId, _hash, DateTimeOffset.UtcNow.AddMinutes(-1));

        (await VerifyAsync(_hash)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Set_RejectsAnythingButAHexHash_AndAnUnknownSession()
    {
        (await SetAsync(new { tokenHash = "the-token-itself", expiresAt = DateTimeOffset.UtcNow.AddHours(1) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetAsync(new { tokenHash = _hash, expiresAt = DateTimeOffset.UtcNow.AddHours(-1) })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetAsync(new { tokenHash = _hash, expiresAt = DateTimeOffset.UtcNow.AddHours(1) }, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
