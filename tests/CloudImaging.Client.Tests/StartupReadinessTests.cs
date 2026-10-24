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
    public async Task OperationSelection_DisablesEverything_WhenTheServiceCannotBeReached()
    {
        var rechecked = false;
        var readiness = new StartupReadiness(1, GatewayReachability.NoNetwork, AutopilotAvailabilityState.Unreachable);
        var vm = Selection(readiness, _ => { rechecked = true; return Task.FromResult(readiness with { Rechecked = true }); });

        vm.IsServiceBlocked.Should().BeTrue();
        vm.IsImagingSelectable.Should().BeFalse();
        vm.IsAutopilotSelectable.Should().BeFalse();
        vm.SelectedOperation.Should().BeNull();
        vm.CanContinue.Should().BeFalse();
        vm.ConnectivityTitle.Should().Be("No network connection");

        vm.SelectOperationCommand.Execute("Imaging");
        vm.SelectedOperation.Should().BeNull();

        await vm.RecheckAsync();
        rechecked.Should().BeTrue();
    }

    [Fact]
    public void OperationSelection_KeepsAutopilot_WhenOnlyTheDiskIsMissing()
    {
        var vm = Selection(new StartupReadiness(0, GatewayReachability.Reachable, AutopilotAvailabilityState.Enabled));

        vm.IsServiceBlocked.Should().BeFalse();
        vm.IsImagingSelectable.Should().BeFalse();
        vm.ImagingCardDescription.Should().StartWith("No local disk detected");
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

    // Boot media update

    private static StartupViewModel ChecksWithUpdate(
        GatewayReachability gateway,
        Func<CancellationToken, Task<BootImageUpdateCheck>> checkUpdate,
        string? adapter = "Intel Ethernet (10.0.0.5)",
        Func<Action<BootImageUpdateProgress>, CancellationToken, Task<bool>>? applyUpdate = null,
        Action? restart = null) =>
        new(_ => Task.FromResult(new GatewayProbeResult(gateway, null)), false,
            findLocalDisks: () => [Disk],
            findNetworkAdapter: () => adapter,
            networkWait: TimeSpan.FromMilliseconds(50),
            checkBootImageUpdate: checkUpdate,
            applyBootImageUpdate: applyUpdate,
            restartDevice: restart);

    [Fact]
    public async Task Update_IsSkipped_WithoutAskingTheService_WhenThereIsNoNetwork()
    {
        var asked = false;
        var vm = ChecksWithUpdate(GatewayReachability.Unreachable, _ => { asked = true; return Task.FromResult(new BootImageUpdateCheck(BootImageUpdateStatus.UpToDate)); }, adapter: null);

        await vm.RunAsync();

        asked.Should().BeFalse("the update check only runs once the network and service are known to work");
        vm.UpdateCheck.State.Should().Be(StartupCheckState.Skipped);
        vm.UpdateCheck.Detail.Should().Be("Skipped: no network.");
        vm.Checks.Should().HaveCount(4);
    }

    [Theory]
    [InlineData(BootImageUpdateStatus.UpToDate, StartupCheckState.Passed, "Up to date (2026.10.1).")]
    [InlineData(BootImageUpdateStatus.Failed, StartupCheckState.Skipped, "Could not check. The current boot image is kept.")]
    public async Task Update_ReportsTheOutcome_WithoutEverFailingStartup(BootImageUpdateStatus status, StartupCheckState expected, string detail)
    {
        var vm = ChecksWithUpdate(GatewayReachability.Reachable, _ => Task.FromResult(new BootImageUpdateCheck(status, "2026.10.1")));

        var readiness = await vm.RunAsync();

        readiness.CanReachService.Should().BeTrue();
        readiness.RestartRequired.Should().BeFalse();
        vm.UpdateCheck.State.Should().Be(expected);
        vm.UpdateCheck.Detail.Should().Be(detail);
    }

    [Fact]
    public async Task Update_HoldsStartup_ShowsProgress_ThenAsksForARestart()
    {
        var seen = new List<(string Detail, int? Percent)>();
        var restarted = false;
        StartupViewModel? vm = null;
        vm = ChecksWithUpdate(GatewayReachability.Reachable,
            _ => Task.FromResult(new BootImageUpdateCheck(BootImageUpdateStatus.UpdateAvailable, "2026.10.2")),
            applyUpdate: (report, ct) =>
            {
                ct.CanBeCanceled.Should().BeFalse("the startup time limit must not cut a boot image download short");
                foreach (var p in new[]
                {
                    new BootImageUpdateProgress(BootImageUpdatePhase.Downloading, 50, 320_000_000, 640_000_000),
                    new BootImageUpdateProgress(BootImageUpdatePhase.Verifying, 40),
                    new BootImageUpdateProgress(BootImageUpdatePhase.Applying, null),
                })
                {
                    report(p);
                    vm!.RestartRequired.Should().BeFalse();
                    seen.Add((vm.UpdateCheck.Detail, vm.UpdateCheck.Percent));
                }
                return Task.FromResult(true);
            },
            restart: () => restarted = true);

        var readiness = await vm.RunAsync();

        seen.Should().Equal(
            ("Downloading 2026.10.2: 320 of 640 MB", 50),
            ("Verifying 2026.10.2…", 40),
            ("Writing 2026.10.2 to the USB drive…", null));
        readiness.RestartRequired.Should().BeTrue();
        vm.RestartRequired.Should().BeTrue();
        vm.UpdateCheck.State.Should().Be(StartupCheckState.Passed);
        vm.UpdateCheck.Detail.Should().Be("Updated to 2026.10.2. Restart the device to start from it.");
        vm.UpdateCheck.Badge.Should().Be("Restart required");
        vm.UpdateCheck.HasPercent.Should().BeFalse();

        vm.RestartCommand.Execute(null);
        restarted.Should().BeTrue();
    }

    [Fact]
    public async Task Update_ThatCannotBeWritten_KeepsTheCurrentImage_AndCarriesOn()
    {
        var vm = ChecksWithUpdate(GatewayReachability.Reachable,
            _ => Task.FromResult(new BootImageUpdateCheck(BootImageUpdateStatus.UpdateAvailable, "2026.10.2")),
            applyUpdate: (_, _) => Task.FromResult(false));

        var readiness = await vm.RunAsync();

        readiness.RestartRequired.Should().BeFalse();
        vm.RestartRequired.Should().BeFalse();
        vm.UpdateCheck.State.Should().Be(StartupCheckState.Skipped);
        vm.UpdateCheck.Detail.Should().Be("Could not update. The current boot image is kept.");
        vm.UpdateCheck.HasBadge.Should().BeFalse();
    }

    [Fact]
    public async Task Update_ThatThrows_IsSkipped()
    {
        var vm = ChecksWithUpdate(GatewayReachability.Reachable, _ => throw new HttpRequestException("No such host is known."));

        await vm.RunAsync();

        vm.UpdateCheck.State.Should().Be(StartupCheckState.Skipped);
    }

    private static OperationSelectionViewModel Selection(StartupReadiness readiness, Func<CancellationToken, Task<StartupReadiness>>? recheck = null) =>
        new(Gateway(_ => new HttpResponseMessage(HttpStatusCode.OK)), (_, _) => { },
            autopilotToolingPresent: true, navigateToAutopilot: () => { },
            autopilotAvailability: readiness.AutopilotAvailability, readiness: readiness, recheck: recheck,
            readNetwork: () => NetworkSnapshot.Empty);

    private static DeviceGatewayApiClient Gateway(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://gw.example.com") });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
