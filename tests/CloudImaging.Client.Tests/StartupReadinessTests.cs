using System.Net;
using System.Net.Http;
using System.Text;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Startup prerequisite checks (local disk, network, Device Gateway) and how Operation Selection
/// gates its operations on them. No real disks, adapters or network are touched.
/// </summary>
public sealed class StartupReadinessTests
{
    private static readonly LocalDisk Disk = new(0, "Samsung SSD 980", 512_110_190_592);

    private static StartupViewModel Checks(
        GatewayProbeResult probe,
        IReadOnlyList<LocalDisk>? disks = null,
        string? adapter = "Intel Ethernet (10.0.0.5)",
        bool autopilotToolingPresent = false) =>
        new(_ => Task.FromResult(probe), autopilotToolingPresent,
            findLocalDisks: () => disks ?? [Disk],
            findNetworkAdapter: () => adapter,
            networkWait: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task Run_PassesEveryCheck_WhenTheDeviceIsReady()
    {
        var vm = Checks(new GatewayProbeResult(GatewayReachability.Reachable, null));

        var readiness = await vm.RunAsync();

        readiness.CanReachService.Should().BeTrue();
        readiness.LocalDiskCount.Should().Be(1);
        vm.Checks.Should().OnlyContain(c => c.State == StartupCheckState.Passed);
        vm.DiskCheck.Detail.Should().Be("Samsung SSD 980 (512 GB)");
        vm.NetworkCheck.Detail.Should().Be("Intel Ethernet (10.0.0.5)");
    }

    [Fact]
    public async Task Run_ReportsNoNetwork_WhenNoAdapterComesUpAndTheGatewayIsUnreachable()
    {
        var vm = Checks(new GatewayProbeResult(GatewayReachability.Unreachable, null), adapter: null);

        var readiness = await vm.RunAsync();

        readiness.Gateway.Should().Be(GatewayReachability.NoNetwork);
        vm.NetworkCheck.State.Should().Be(StartupCheckState.Failed);
        vm.ServiceCheck.State.Should().Be(StartupCheckState.Failed);
    }

    [Fact]
    public async Task Run_TrustsTheGateway_WhenItAnswersWithoutAnIpv4Adapter()
    {
        var vm = Checks(new GatewayProbeResult(GatewayReachability.Reachable, null), adapter: null);

        var readiness = await vm.RunAsync();

        readiness.CanReachService.Should().BeTrue("an IPv6-only network can still reach the gateway");
        vm.NetworkCheck.State.Should().Be(StartupCheckState.Passed);
    }

    [Fact]
    public async Task Run_KeepsGatewayFailuresDistinct_WhenTheDeviceIsOnline()
    {
        var vm = Checks(new GatewayProbeResult(GatewayReachability.ServiceUnavailable, null));

        var readiness = await vm.RunAsync();

        readiness.Gateway.Should().Be(GatewayReachability.ServiceUnavailable);
        vm.NetworkCheck.State.Should().Be(StartupCheckState.Passed);
        vm.ServiceCheck.State.Should().Be(StartupCheckState.Failed);
    }

    [Theory]
    [InlineData(0, StartupCheckState.Failed)]
    [InlineData(2, StartupCheckState.Warning)]
    public async Task Run_FlagsADiskCountImagingCannotUse(int diskCount, StartupCheckState expected)
    {
        var disks = Enumerable.Range(0, diskCount).Select(i => Disk with { Index = i }).ToList();
        var vm = Checks(new GatewayProbeResult(GatewayReachability.Reachable, null), disks);

        var readiness = await vm.RunAsync();

        readiness.LocalDiskCount.Should().Be(diskCount);
        vm.DiskCheck.State.Should().Be(expected);
    }

    [Fact]
    public async Task Run_DoesNotBlock_WhenTheDisksCannotBeRead()
    {
        var vm = new StartupViewModel(_ => Task.FromResult(new GatewayProbeResult(GatewayReachability.Reachable, null)), false,
            findLocalDisks: () => throw new InvalidOperationException("WMI unavailable"),
            findNetworkAdapter: () => "Ethernet (10.0.0.5)");

        var readiness = await vm.RunAsync();

        readiness.LocalDiskCount.Should().BeNull();
        vm.DiskCheck.State.Should().Be(StartupCheckState.Warning);
    }

    [Theory]
    [InlineData(true, AutopilotAvailabilityState.Enabled)]
    [InlineData(false, AutopilotAvailabilityState.Disabled)]
    [InlineData(null, AutopilotAvailabilityState.Unreachable)]
    public async Task Run_ResolvesAutopilotFromTheSameProbe(bool? enabled, AutopilotAvailabilityState expected)
    {
        var vm = Checks(new GatewayProbeResult(GatewayReachability.Reachable, enabled), autopilotToolingPresent: true);

        (await vm.RunAsync()).AutopilotAvailability.Should().Be(expected);
    }

    // Device Gateway probe

    [Theory]
    [InlineData(HttpStatusCode.OK, GatewayReachability.Reachable)]
    [InlineData(HttpStatusCode.NotFound, GatewayReachability.Reachable)]
    [InlineData(HttpStatusCode.Unauthorized, GatewayReachability.CertificateRejected)]
    [InlineData(HttpStatusCode.Forbidden, GatewayReachability.CertificateRejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GatewayReachability.ServiceUnavailable)]
    public async Task Probe_ClassifiesTheGatewayAnswer(HttpStatusCode status, GatewayReachability expected)
    {
        var gateway = Gateway(_ => new HttpResponseMessage(status) { Content = new StringContent("""{"enabled":true}""", Encoding.UTF8, "application/json") });

        var result = await gateway.ProbeAsync();

        result.Reachability.Should().Be(expected);
        result.AutopilotEnabled.Should().Be(status == HttpStatusCode.OK ? true : null);
    }

    [Fact]
    public async Task Probe_ReportsUnreachable_WhenTheHostCannotBeResolved()
    {
        var gateway = Gateway(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."));

        (await gateway.ProbeAsync()).Reachability.Should().Be(GatewayReachability.Unreachable);
    }

    [Fact]
    public async Task Probe_ReportsNotConfigured_WithoutAServiceAddress()
    {
        var gateway = new DeviceGatewayApiClient(new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Should not be called."))));

        (await gateway.ProbeAsync()).Reachability.Should().Be(GatewayReachability.NotConfigured);
    }

    // Operation Selection gating

    [Fact]
    public void OperationSelection_DisablesEverything_WhenTheServiceCannotBeReached()
    {
        var rechecked = false;
        var vm = Selection(new StartupReadiness(1, GatewayReachability.NoNetwork, AutopilotAvailabilityState.Unreachable), () => rechecked = true);

        vm.IsServiceBlocked.Should().BeTrue();
        vm.IsImagingSelectable.Should().BeFalse();
        vm.IsAutopilotSelectable.Should().BeFalse();
        vm.SelectedOperation.Should().BeNull();
        vm.CanContinue.Should().BeFalse();
        vm.ConnectivityTitle.Should().Be("Network connection required");

        vm.SelectOperationCommand.Execute("Imaging");
        vm.SelectedOperation.Should().BeNull();

        vm.RecheckCommand.Execute(null);
        rechecked.Should().BeTrue();
    }

    [Fact]
    public void OperationSelection_KeepsAutopilot_WhenOnlyTheDiskIsMissing()
    {
        var vm = Selection(new StartupReadiness(0, GatewayReachability.Reachable, AutopilotAvailabilityState.Enabled));

        vm.IsServiceBlocked.Should().BeFalse();
        vm.IsImagingSelectable.Should().BeFalse();
        vm.ImagingCardDescription.Should().StartWith("No local disk found");
        vm.IsAutopilotSelectable.Should().BeTrue("registering a hash never touches the disk");
        vm.SelectedOperation.Should().BeNull();

        vm.SelectOperationCommand.Execute("Autopilot");
        vm.CanContinue.Should().BeTrue();
    }

    [Fact]
    public void OperationSelection_PreselectsImaging_WhenEverythingIsReady()
    {
        var vm = Selection(new StartupReadiness(1, GatewayReachability.Reachable, AutopilotAvailabilityState.Enabled));

        vm.SelectedOperation.Should().Be("Imaging");
        vm.CanContinue.Should().BeTrue();
        vm.IsServiceBlocked.Should().BeFalse();
    }

    private static OperationSelectionViewModel Selection(StartupReadiness readiness, Action? recheck = null) =>
        new(Gateway(_ => new HttpResponseMessage(HttpStatusCode.OK)), (_, _) => { },
            autopilotToolingPresent: true, navigateToAutopilot: () => { },
            autopilotAvailability: readiness.AutopilotAvailability, readiness: readiness, recheck: recheck);

    private static DeviceGatewayApiClient Gateway(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://gw.example.com") });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
