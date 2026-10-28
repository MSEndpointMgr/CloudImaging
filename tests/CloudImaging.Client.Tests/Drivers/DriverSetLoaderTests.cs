using System.IO;
using System.Net;
using System.Net.Http;
using CloudImaging.Client.Services.Drivers;
using CloudImaging.Client.Tests.TestDoubles;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests.Drivers;

public sealed class DriverSetLoaderTests : IDisposable
{
    private readonly DriverStepFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task Load_200_ReturnsTheDescriptor()
    {
        _f.ServeDescriptor(DriverStepFixture.Set(_f.Component(0, "sp200")));

        var result = await _f.Loader.LoadAsync(DriverStepFixture.SessionId);

        result.Status.Should().Be(DriverSetLoadStatus.Loaded);
        result.Descriptor!.SetVersion.Should().Be(7);
        result.Descriptor.Release.Should().Be(WindowsRelease.Windows11Version24H2);
        result.Descriptor.Components.Single().Extract.Should().Be(DriverExtractKind.Hp);
        _f.Gateway.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be($"/api/v1/sessions/{DriverStepFixture.SessionId}/driver-set");
    }

    [Fact]
    public async Task Load_404_DoesNotShowTheStep()
    {
        (await _f.Loader.LoadAsync(DriverStepFixture.SessionId)).Status.Should().Be(DriverSetLoadStatus.NotShown);
    }

    [Fact]
    public async Task Load_409_MeansTheSessionIsEnding()
    {
        _f.Descriptor = () => new HttpResponseMessage(HttpStatusCode.Conflict);

        (await _f.Loader.LoadAsync(DriverStepFixture.SessionId)).Status.Should().Be(DriverSetLoadStatus.PipelineEnding);
    }

    [Fact]
    public async Task Load_ThreeFailedAttempts_Fails_WithWaitsOfTwoAndFourSeconds()
    {
        _f.Descriptor = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        var result = await _f.Loader.LoadAsync(DriverStepFixture.SessionId);

        result.Status.Should().Be(DriverSetLoadStatus.Failed);
        _f.Gateway.Requests.Should().HaveCount(3);
        _f.Waits.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Load_RateLimited_CountsAsAnAttempt_AndWaitsAsTold()
    {
        var calls = 0;
        _f.Descriptor = () =>
        {
            if (++calls == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
                return limited;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        };

        (await _f.Loader.LoadAsync(DriverStepFixture.SessionId)).Status.Should().Be(DriverSetLoadStatus.NotShown);
        _f.Waits.Should().Equal(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public async Task Download_StreamsAndVerifiesTheSha256()
    {
        var component = _f.Component(0, "sp200");
        var path = Path.Combine(_f.Root, "sp200.exe");

        var result = await _f.Loader.DownloadAsync(DriverStepFixture.SessionId, component, path);

        result.Succeeded.Should().BeTrue();
        File.ReadAllBytes(path).Should().Equal(_f.Files[component.DownloadUrl]);
        _f.Storage.Requests.Single().Request.Headers.Authorization.Should().BeNull("read links carry their own signature, never the session token");
    }

    [Fact]
    public async Task Download_ChecksumMismatchThreeTimes_FailsAndLeavesNoFile()
    {
        var component = _f.Component(0, "sp200") with { Sha256 = new string('0', 64) };
        var path = Path.Combine(_f.Root, "sp200.exe");

        var result = await _f.Loader.DownloadAsync(DriverStepFixture.SessionId, component, path);

        result.Should().Be(new DriverDownloadResult(false, "The downloaded file does not match its checksum."));
        File.Exists(path).Should().BeFalse();
        _f.Storage.Requests.Should().HaveCount(3);
        _f.Waits.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task Download_403_RefreshesTheLinkOnce_WithoutSpendingAnAttempt()
    {
        var component = _f.Component(0, "sp200");
        _f.StorageOverride = request => request.RequestUri!.Host == "storage.test" && request.RequestUri.AbsolutePath.StartsWith("/drivers/", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : null;

        var result = await _f.Loader.DownloadAsync(DriverStepFixture.SessionId, component, Path.Combine(_f.Root, "sp200.exe"));

        result.Succeeded.Should().BeTrue();
        _f.Gateway.Requests.Single().Request.RequestUri!.AbsolutePath.Should().Be($"/api/v1/sessions/{DriverStepFixture.SessionId}/driver-set/components/0/link");
        _f.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task Download_LinkExpiringWithinTwoMinutes_IsReplacedFirst()
    {
        var component = _f.Component(0, "sp200", expiresAt: DriverStepFixture.Now.AddSeconds(90));

        var result = await _f.Loader.DownloadAsync(DriverStepFixture.SessionId, component, Path.Combine(_f.Root, "sp200.exe"));

        result.Succeeded.Should().BeTrue();
        _f.Gateway.Requests.Should().ContainSingle(r => r.Request.Method == HttpMethod.Post);
        _f.Storage.Requests.Single().Request.RequestUri!.ToString().Should().Be("https://storage.test/refreshed/0");
    }
}
