using System.Net;
using System.Net.Http;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Unit tests for <see cref="SessionInitViewModel"/>'s status-poll state-machine handling
/// (FR-005, FR-009d).
///
/// Regression test (2026-08-25): <c>PollOnceAsync</c> originally grouped
/// <see cref="CloudImaging.Contracts.Enums.SessionState.SessionAssigned"/> together with
/// <c>SessionStarted</c>/<c>SessionInProgress</c> in the same case that hands off to the imaging
/// progress view. But <c>SessionAssigned</c> only means the device was coupled by passcode — no
/// OS image has been chosen yet and the portal operator has not clicked "Start Imaging". This
/// made the Client jump to the Format Disk/progress screen immediately upon coupling, before an
/// operator ever assigned an image. These tests pin the corrected behavior: only
/// <c>SessionStarted</c>/<c>SessionInProgress</c> hand off to the progress view; <c>SessionAssigned</c>
/// must keep polling on the waiting screen.
/// </summary>
public sealed class SessionInitViewModelTests
{
    private static SessionInitViewModel CreateViewModel(
        string state,
        out List<(SessionStatusResponse Session, string? Serial)> progressCalls,
        out List<(ResultsViewModel.Outcome Outcome, string? Serial, string? Detail, string? Log)> resultsCalls)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"sessionId":"11111111-1111-1111-1111-111111111111","state":"{{state}}","currentStep":null,"overallProgressPercent":0}""",
                System.Text.Encoding.UTF8, "application/json"),
        });
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });

        var localProgressCalls = new List<(SessionStatusResponse, string?)>();
        var localResultsCalls = new List<(ResultsViewModel.Outcome, string?, string?, string?)>();

        var vm = new SessionInitViewModel(
            gateway,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            passcode: "ABC123",
            deviceSerialNumber: "SN-TEST",
            navigateToResults: (outcome, serial, detail, log) => localResultsCalls.Add((outcome, serial, detail, log)),
            navigateToProgress: (session, serial) => localProgressCalls.Add((session, serial)));

        progressCalls = localProgressCalls;
        resultsCalls = localResultsCalls;
        return vm;
    }

    /// <summary>Spins briefly for the fire-and-forget background poll loop to run once.</summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task SessionAssigned_DoesNotNavigateToProgress_AndKeepsPolling()
    {
        using var vm = CreateViewModel("SessionAssigned", out var progressCalls, out var resultsCalls);

        await WaitForAsync(() => vm.StatusMessage != null && vm.StatusMessage.Contains("coupled", StringComparison.OrdinalIgnoreCase));

        progressCalls.Should().BeEmpty("coupling alone (SessionAssigned) must not hand off to the imaging pipeline");
        resultsCalls.Should().BeEmpty();
        vm.IsPolling.Should().BeTrue("the Client must keep polling until an image is actually assigned");
        vm.StatusMessage.Should().Contain("coupled", "the status text should reflect that the device is coupled, not still awaiting coupling");
    }

    [Theory]
    [InlineData("SessionStarted")]
    [InlineData("SessionInProgress")]
    public async Task SessionStartedOrInProgress_NavigatesToProgress(string state)
    {
        using var vm = CreateViewModel(state, out var progressCalls, out var resultsCalls);

        await WaitForAsync(() => progressCalls.Count > 0);

        progressCalls.Should().HaveCount(1, $"{state} means an OS image was actually assigned and imaging should begin");
        resultsCalls.Should().BeEmpty();
        vm.IsPolling.Should().BeFalse("polling stops once handed off to the imaging pipeline");
    }

    [Fact]
    public async Task SessionExpired_NavigatesToResults_WithExpiredOutcome()
    {
        // FR-021: a session that timed out before an operator coupled it must present as a
        // benign timeout (Outcome.Expired), never as Outcome.Failure ("Imaging Failed") — no
        // imaging was ever attempted.
        using var vm = CreateViewModel("SessionExpired", out var progressCalls, out var resultsCalls);

        await WaitForAsync(() => resultsCalls.Count > 0);

        progressCalls.Should().BeEmpty();
        resultsCalls.Should().ContainSingle().Which.Outcome.Should().Be(ResultsViewModel.Outcome.Expired);
        vm.IsPolling.Should().BeFalse("polling stops once a terminal state is reached");
    }

    /// <summary>
    /// Regression test: nothing previously guarded <c>PollOnceAsync</c> against concurrent
    /// invocation — the manual RefreshCommand racing the automatic 30s poll loop could both
    /// observe a hand-off state (e.g. SessionStarted) and each independently fire
    /// <c>navigateToProgress</c>, constructing a second <c>ImagingWorkflowViewModel</c> that
    /// restarted the imaging pipeline from Format Disk and replaced the already-progressing
    /// ProgressView — visible to the user as the Format Disk step "reverting" to active right
    /// after Download had already begun. Only the first caller to observe the hand-off state may
    /// navigate, no matter how many polls race to get there.
    /// </summary>
    [Fact]
    public async Task ConcurrentPolls_OnlyNavigateToProgressOnce()
    {
        using var vm = CreateViewModel("SessionStarted", out var progressCalls, out var resultsCalls);

        // Simulate a user mashing "Refresh Status" concurrently with the automatic first poll.
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(() => vm.RefreshCommand.Execute(null)))
            .ToArray();
        await Task.WhenAll(tasks);

        await WaitForAsync(() => progressCalls.Count > 0);
        await Task.Delay(100); // give any (buggy) duplicate navigation time to land

        progressCalls.Should().HaveCount(1,
            "only one navigation to the progress view may occur, regardless of how many concurrent polls raced to the hand-off state");
        resultsCalls.Should().BeEmpty();
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
