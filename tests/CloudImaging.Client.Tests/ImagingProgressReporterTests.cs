using System.Net;
using System.Net.Http;
using CloudImaging.Client.Services;
using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Regression coverage for the Device Gateway 429 flood (2026-08-30): a percent-progress
/// callback fires on every download chunk / DISM console line, so <see cref="ImagingProgressReporter"/>
/// must throttle those specifically, without any percent value (including 0/100) bypassing the
/// throttle — that exact bypass is what caused the flood in production.
/// </summary>
public sealed class ImagingProgressReporterTests
{
    [Fact]
    public async Task ReportAsync_RepeatedZeroPercentWithinInterval_SendsOnlyOnce()
    {
        var (handler, gateway) = CreateGateway();
        var clock = new FakeTimeProvider();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, clock);

        // Simulates a large download where integer-truncated percent stays at 0 for hundreds of
        // ~80 KB chunk reads before crossing 1% — the scenario that flooded the gateway.
        for (var i = 0; i < 50; i++)
        {
            await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 0);
        }

        handler.CallCount.Should().Be(1, "only the first update for a new step may bypass the throttle");
    }

    [Fact]
    public async Task ReportAsync_RepeatedHundredPercentWithinInterval_SendsOnlyOnce()
    {
        var (handler, gateway) = CreateGateway();
        var clock = new FakeTimeProvider();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, clock);

        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 50);
        for (var i = 0; i < 20; i++)
        {
            await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 100);
        }

        handler.CallCount.Should().Be(1, "100% must not bypass the throttle any more than any other value");
    }

    [Fact]
    public async Task ReportAsync_AfterThrottleIntervalElapses_SendsAgain()
    {
        var (handler, gateway) = CreateGateway();
        var clock = new FakeTimeProvider();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, clock);

        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 0);
        handler.CallCount.Should().Be(1);

        // Must exceed ImagingProgressReporter's private MinProgressReportInterval — keep in sync
        // with that constant if it ever changes.
        clock.UtcNow += TimeSpan.FromSeconds(11);
        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 1);

        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task ReportAsync_NewStep_AlwaysSendsImmediatelyEvenWithinInterval()
    {
        var (handler, gateway) = CreateGateway();
        var clock = new FakeTimeProvider();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, clock);

        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 50);
        await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress, 0);

        handler.CallCount.Should().Be(2, "a step transition must always report immediately regardless of timing");
    }

    [Fact]
    public async Task ReportAsync_CompletedStatus_IsNeverThrottled()
    {
        var (handler, gateway) = CreateGateway();
        var clock = new FakeTimeProvider();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, clock);

        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 0);
        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.Completed);

        handler.CallCount.Should().Be(2, "step start/completion reports are one-off events and must never be throttled");
    }

    private static (CountingHttpMessageHandler Handler, DeviceGatewayApiClient Gateway) CreateGateway()
    {
        var handler = new CountingHttpMessageHandler();
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });
        return (handler, gateway);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class CountingHttpMessageHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
