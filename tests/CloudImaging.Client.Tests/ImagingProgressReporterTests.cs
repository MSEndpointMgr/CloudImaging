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
/// throttle: that exact bypass is what caused the flood in production.
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
        // ~80 KB chunk reads before crossing 1%: the scenario that flooded the gateway.
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

        // Must exceed ImagingProgressReporter's private MinProgressReportInterval: keep in sync
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

    [Fact]
    public async Task ReportResultAsync_SendsWarningCountAndItemsInOneCall()
    {
        var handler = new TestDoubles.RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, new FakeTimeProvider());
        CloudImaging.Contracts.Models.SetupProfiles.ImagingStepItem[] items =
        [
            new() { Index = 0, Kind = StepItemKind.RegistryValue, Name = "Time zone", Outcome = StepItemOutcome.Succeeded },
            new() { Index = 1, Kind = StepItemKind.AnswerFile, Name = "Answer file", Outcome = StepItemOutcome.Failed, Reason = "The disk is full." },
        ];

        await reporter.ReportResultAsync(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed, 1, items);

        handler.Requests.Should().ContainSingle();
        using var body = System.Text.Json.JsonDocument.Parse(handler.Requests[0].Body!);
        body.RootElement.GetProperty("stepName").GetString().Should().Be("PrepareSetup");
        body.RootElement.GetProperty("status").GetString().Should().Be("Completed");
        body.RootElement.GetProperty("warningCount").GetInt32().Should().Be(1);
        var sent = body.RootElement.GetProperty("items");
        sent.GetArrayLength().Should().Be(2);
        sent[1].GetProperty("kind").GetString().Should().Be("AnswerFile");
        sent[1].GetProperty("outcome").GetString().Should().Be("Failed");
    }

    [Fact]
    public async Task ReportAsync_SendsTheStepNote_AndAnEmptyNoteClearsIt()
    {
        var handler = new TestDoubles.RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, new FakeTimeProvider());

        await reporter.ReportAsync(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress, statusNote: "Waiting for the AC adapter");
        await reporter.ReportAsync(ImagingStepName.FormatDisk, ImagingStepStatus.InProgress, statusNote: string.Empty);
        await reporter.ReportAsync(ImagingStepName.FormatDisk, ImagingStepStatus.Completed);

        string? Note(int index)
        {
            using var body = System.Text.Json.JsonDocument.Parse(handler.Requests[index].Body!);
            var note = body.RootElement.GetProperty("statusNote");
            return note.ValueKind == System.Text.Json.JsonValueKind.Null ? null : note.GetString();
        }

        handler.Requests.Should().HaveCount(3);
        Note(0).Should().Be("Waiting for the AC adapter");
        Note(1).Should().BeEmpty();
        Note(2).Should().BeNull("a report without a note leaves the stored note as it is");
    }

    [Fact]
    public async Task ReportResultAsync_IsNeverThrottled_AndPercentUpdatesStayThrottled()
    {
        var (handler, gateway) = CreateGateway();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, new FakeTimeProvider());

        await reporter.ReportAsync(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress, 10);
        await reporter.ReportAsync(ImagingStepName.PrepareSetup, ImagingStepStatus.InProgress, 20);
        await reporter.ReportResultAsync(ImagingStepName.PrepareSetup, ImagingStepStatus.Completed, 0, []);

        handler.CallCount.Should().Be(2, "the second percent update falls inside the interval; the result always goes through");
    }

    [Fact]
    public async Task ReportAsync_PercentUpdateRightAfterTheStepStart_WaitsForTheInterval()
    {
        var (handler, gateway) = CreateGateway();
        var clock = new FakeTimeProvider();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, clock);

        await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.InProgress);
        await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.InProgress, 0);
        handler.CallCount.Should().Be(1, "the start report already tells the portal the step runs");

        clock.UtcNow += TimeSpan.FromSeconds(11);
        await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.InProgress, 40);
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task ReportAsync_StepEnd_IsSentAgainAfterTheGatewaysRetryAfter()
    {
        var (handler, reporter, waits) = Scripted(RateLimited(7), Delivered());

        await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.Completed);

        handler.Requests.Should().HaveCount(2, "without the end report the session never completes in the portal");
        waits.Should().Equal(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task ARateLimitWithoutRetryAfter_WaitsOutTheWholeWindow()
    {
        var (handler, reporter, waits) = Scripted(new HttpResponseMessage(HttpStatusCode.TooManyRequests), Delivered());

        await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.Completed);

        handler.Requests.Should().HaveCount(2);
        waits.Should().Equal(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task ReportResultAsync_IsSentAgainAfterAServerError()
    {
        var (handler, reporter, waits) = Scripted(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), Delivered());

        await reporter.ReportResultAsync(ImagingStepName.InstallDrivers, ImagingStepStatus.Completed, 0, []);

        handler.Requests.Should().HaveCount(2);
        waits.Should().ContainSingle();
    }

    [Fact]
    public async Task ReportAsync_StepEnd_GivesUpAfterThreeAttempts_WithoutThrowing()
    {
        var (handler, reporter, _) = Scripted(RateLimited(1), RateLimited(1), RateLimited(1));

        await reporter.ReportAsync(ImagingStepName.ConfigureBoot, ImagingStepStatus.Failed, errorDetail: "bcdboot failed");

        handler.Requests.Should().HaveCount(3, "reporting stays best effort: imaging carries on");
    }

    [Fact]
    public async Task ReportAsync_PercentUpdate_IsNotSentAgain()
    {
        var (handler, reporter, waits) = Scripted(RateLimited(7));

        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 30);

        handler.Requests.Should().ContainSingle("the next percent update replaces it anyway");
        waits.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportAsync_Rejection_IsNotSentAgain()
    {
        var (handler, reporter, waits) = Scripted(new HttpResponseMessage(HttpStatusCode.Conflict));

        await reporter.ReportAsync(ImagingStepName.ApplyRecoveryImage, ImagingStepStatus.Completed);

        handler.Requests.Should().ContainSingle("an ended session refuses every report");
        waits.Should().BeEmpty();
    }

    [Fact]
    public async Task StepEnd_WaitsForAPercentUpdateStillOnItsWay()
    {
        var handler = new SlowPercentHandler();
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, new FakeTimeProvider());

        // As in the pipeline: a percent update nobody waits for, then the step's end report.
        var percent = reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 99);
        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.Completed);
        await percent;

        handler.EndArrivedWhilePercentInFlight.Should().BeFalse("Core stores whichever report reaches it last");
        handler.Arrived.Should().Equal("InProgress", "Completed");
    }

    [Fact]
    public async Task StartOrPercentUpdate_MadeAfterItsStepEnded_IsNotSent()
    {
        var (handler, gateway) = CreateGateway();
        var clock = new FakeTimeProvider();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, clock);

        await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress);
        await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.Completed);
        clock.UtcNow += TimeSpan.FromSeconds(11);
        await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress, 100);
        await reporter.ReportAsync(ImagingStepName.ApplyImage, ImagingStepStatus.InProgress);

        handler.CallCount.Should().Be(2, "either would show the finished step running again");
    }

    [Fact]
    public async Task StepEnd_IsStillSent_WhenTheReportBeforeItFailed()
    {
        var (handler, reporter, _) = Scripted(new HttpResponseMessage(HttpStatusCode.InternalServerError), Delivered());

        var percent = reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.InProgress, 50);
        await reporter.ReportAsync(ImagingStepName.DownloadImage, ImagingStepStatus.Completed);
        await percent;

        handler.Requests.Should().HaveCount(2, "a report that failed must not hold up the ones after it");
    }

    private static (TestDoubles.RecordingHandler Handler, ImagingProgressReporter Reporter, List<TimeSpan> Waits) Scripted(params HttpResponseMessage[] responses)
    {
        var queue = new Queue<HttpResponseMessage>(responses);
        var handler = new TestDoubles.RecordingHandler(_ => queue.Dequeue());
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });
        var waits = new List<TimeSpan>();
        var reporter = new ImagingProgressReporter(gateway, Guid.NewGuid(), NullLogger<ImagingProgressReporter>.Instance, new FakeTimeProvider(), (wait, _) =>
        {
            waits.Add(wait);
            return Task.CompletedTask;
        });
        return (handler, reporter, waits);
    }

    internal static HttpResponseMessage RateLimited(int seconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private static HttpResponseMessage Delivered() => new(HttpStatusCode.NoContent);

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

    /// <summary>
    /// A slow network for percent updates: each takes half a second, unless an end report overtakes
    /// it first. Records the order reports arrive in, and whether an end report arrived while a
    /// percent update was still on its way.
    /// </summary>
    private sealed class SlowPercentHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _endArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _percentInFlight;

        public List<string> Arrived { get; } = [];

        public bool EndArrivedWhilePercentInFlight { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var status = body.RootElement.GetProperty("status").GetString()!;
            lock (Arrived)
            {
                Arrived.Add(status);
            }

            if (status == nameof(ImagingStepStatus.InProgress))
            {
                Interlocked.Increment(ref _percentInFlight);
                await Task.WhenAny(_endArrived.Task, Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken));
                Interlocked.Decrement(ref _percentInFlight);
            }
            else
            {
                EndArrivedWhilePercentInFlight |= Volatile.Read(ref _percentInFlight) > 0;
                _endArrived.TrySetResult();
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
