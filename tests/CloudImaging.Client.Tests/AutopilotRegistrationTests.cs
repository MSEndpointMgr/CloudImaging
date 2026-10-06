using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CloudImaging.Client.Services;
using CloudImaging.Client.ViewModels;
using CloudImaging.Contracts.Enums;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// "Register with Autopilot": hash capture through OA3Tool, the selection card's gating, and the
/// submit then wait-for-decision flow. Uses a fake OA3Tool runner and a scripted HTTP handler, so
/// nothing touches real hardware or the network.
/// </summary>
public sealed class AutopilotRegistrationTests : IDisposable
{
    private const string Hash = "VGhpcyBpcyBhIGZha2UgNEsgaGFyZHdhcmUgaGFzaA==";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ci-autopilot-tests-{Guid.NewGuid():N}");

    public AutopilotRegistrationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "client", AutopilotHashCaptureService.ToolRelativeDirectory));
        Directory.CreateDirectory(Path.Combine(_root, "system32"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void StageTooling()
    {
        File.WriteAllText(Path.Combine(_root, "client", AutopilotHashCaptureService.ToolRelativeDirectory, AutopilotHashCaptureService.Oa3ToolFileName), "stub");
        File.WriteAllText(Path.Combine(_root, "system32", AutopilotHashCaptureService.TpmProviderFileName), "stub");
    }

    private AutopilotHashCaptureService Capture(Func<string, string, string, TimeSpan, CancellationToken, Task<(int, string)>> runner) =>
        new(clientDirectory: Path.Combine(_root, "client"), systemDirectory: Path.Combine(_root, "system32"), runProcessOverride: runner);

    private static Task<(int, string)> WritesReport(string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        if (fileName.EndsWith(AutopilotHashCaptureService.Oa3ToolFileName, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(Path.Combine(workingDirectory, "OA3.xml"), $"<Key><ProductKey>X</ProductKey><HardwareHash>{Hash}</HardwareHash></Key>");
        }
        return Task.FromResult((0, "ok"));
    }

    [Fact]
    public void ParseHardwareHash_ReadsTheHashElement()
    {
        AutopilotHashCaptureService.ParseHardwareHash($"<?xml version=\"1.0\"?><Key><HardwareHash> {Hash} </HardwareHash></Key>").Should().Be(Hash);
    }

    [Theory]
    [InlineData("<Key></Key>")]
    [InlineData("<Key><HardwareHash>not base64!</HardwareHash></Key>")]
    public void ParseHardwareHash_RejectsMissingOrInvalidHashes(string xml)
    {
        var act = () => AutopilotHashCaptureService.ParseHardwareHash(xml);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Capture_RegistersTheTpmProvider_RunsOa3Tool_AndDeletesItsReport()
    {
        StageTooling();
        var calls = new List<(string File, string Args, string WorkingDirectory)>();
        var service = Capture(async (file, args, dir, timeout, ct) =>
        {
            calls.Add((file, args, dir));
            return await WritesReport(file, args, dir, timeout, ct);
        });

        var hash = await service.CaptureAsync();

        hash.Should().Be(Hash);
        calls[0].File.Should().EndWith("rundll32.exe");
        calls[0].Args.Should().Contain("PCPKsp.dll").And.EndWith(",DllInstall");
        calls[1].Args.Should().Be("/Report /ConfigFile=.\\OA3.cfg /NoKeyCheck");
        Directory.Exists(calls[1].WorkingDirectory).Should().BeFalse("the report holds the full hardware inventory");
    }

    [Fact]
    public async Task Capture_SurfacesOa3ToolOutput_WhenItFails()
    {
        StageTooling();
        var service = Capture((file, _, _, _, _) => Task.FromResult(file.EndsWith("rundll32.exe", StringComparison.OrdinalIgnoreCase) ? (0, "") : (5, "Access denied to SMBIOS")));

        var act = () => service.CaptureAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("exit code 5").And.Contain("Access denied to SMBIOS");
    }

    [Fact]
    public async Task Capture_RefusesToRun_WithoutTheTooling()
    {
        var service = Capture(WritesReport);

        service.IsToolingPresent.Should().BeFalse();
        await service.Invoking(s => s.CaptureAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void SelectionCard_IsHidden_WithoutTheTooling()
    {
        var vm = new OperationSelectionViewModel(new DeviceGatewayApiClient(new HttpClient(new ScriptedHandler()) { BaseAddress = new Uri("https://gw.example.com") }), (_, _) => { }, autopilotToolingPresent: false, navigateToAutopilot: () => { });

        vm.IsAutopilotOptionVisible.Should().BeFalse();
        vm.SelectOperationCommand.Execute("Autopilot");
        vm.SelectedOperation.Should().Be("Imaging");
    }

    [Theory]
    [InlineData(true, AutopilotAvailabilityState.Enabled, true)]
    [InlineData(false, AutopilotAvailabilityState.Disabled, false)]
    public async Task SelectionCard_FollowsThePortalSetting(bool enabled, AutopilotAvailabilityState expected, bool selectable)
    {
        var handler = new ScriptedHandler();
        handler.Respond("GET /api/v1/autopilot/availability", HttpStatusCode.OK, new { enabled });
        var navigated = false;
        var vm = new OperationSelectionViewModel(new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }), (_, _) => { }, autopilotToolingPresent: true, navigateToAutopilot: () => navigated = true);

        await WaitUntilAsync(() => vm.AutopilotAvailability != AutopilotAvailabilityState.Checking);

        vm.AutopilotAvailability.Should().Be(expected);
        vm.IsAutopilotSelectable.Should().Be(selectable);
        vm.SelectOperationCommand.Execute("Autopilot");
        vm.ContinueCommand.Execute(null);
        navigated.Should().Be(selectable);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Flow_WarnsTheTechnician_WhenTheHashCannotSupportPreProvisioning(bool withEkPub, bool expectWarning)
    {
        StageTooling();
        var hash = HashWithTpm(withEkPub);
        var handler = new ScriptedHandler();
        handler.Respond("POST /api/v1/autopilot/registrations", HttpStatusCode.Created, new { requestId = Guid.NewGuid(), referenceCode = "AP-7K3Q9", state = "PendingApproval", statusToken = "t" });
        var vm = new AutopilotRegistrationViewModel(
            new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }),
            Capture((file, args, dir, timeout, ct) =>
            {
                if (file.EndsWith(AutopilotHashCaptureService.Oa3ToolFileName, StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(Path.Combine(dir, "OA3.xml"), $"<Key><HardwareHash>{hash}</HardwareHash></Key>");
                return Task.FromResult((0, "ok"));
            }),
            navigateBack: () => { },
            new SystemClockSynchronizationService(ntpServers: []),
            readIdentity: () => new DeviceIdentity("SN-1", "Contoso", "Laptop 7", null, null));

        vm.Start();
        await WaitUntilAsync(() => vm.Stage == AutopilotFlowStage.Submitted);

        vm.HasTpmWarning.Should().Be(expectWarning);
        if (expectWarning) vm.TpmWarning.Should().Contain("pre-provisioning");
    }

    /// <summary>A hash in the 4K record layout with TPM 2.0 and, optionally, the endorsement key.</summary>
    private static string HashWithTpm(bool withEkPub)
    {
        var records = new List<(ushort Type, byte[] Value)> { (1, new byte[24]), (13, "2.0\0"u8.ToArray()) };
        if (withEkPub) records.Add((25, [1, 2, 3, 4]));
        records.Add((0x5343, new byte[32]));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("HH"u8.ToArray());
        writer.Write((ushort)0);
        foreach (var (type, value) in records)
        {
            writer.Write(type);
            writer.Write((ushort)(value.Length + 4));
            writer.Write(value);
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    [Fact]
    public async Task Flow_SubmitsTheHash_ThenReportsTheDecision()
    {
        StageTooling();
        var requestId = Guid.NewGuid();
        var handler = new ScriptedHandler();
        handler.Respond("POST /api/v1/autopilot/registrations", HttpStatusCode.Created, new { requestId, referenceCode = "AP-7K3Q9", state = "PendingApproval", statusToken = "status-token" });
        handler.Respond($"GET /api/v1/autopilot/registrations/{requestId}", HttpStatusCode.OK, new { requestId, referenceCode = "AP-7K3Q9", state = "Imported", groupTag = "EMEA-SE-STD" });
        var gateway = new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") });
        var vm = new AutopilotRegistrationViewModel(
            gateway,
            Capture(WritesReport),
            navigateBack: () => { },
            new SystemClockSynchronizationService(ntpServers: []),
            readIdentity: () => new DeviceIdentity("SN-1", "Contoso", "Laptop 7", null, "Stockholm HQ"),
            pollInterval: TimeSpan.FromMilliseconds(10));

        vm.Start();
        await WaitUntilAsync(() => vm.Stage == AutopilotFlowStage.Submitted);

        vm.ReferenceCode.Should().Be("AP-7K3Q9");
        vm.SerialNumber.Should().Be("SN-1");
        vm.LocationDisplay.Should().Be("Stockholm HQ");
        using (var body = JsonDocument.Parse(handler.Bodies["POST /api/v1/autopilot/registrations"]))
        {
            body.RootElement.GetProperty("hardwareHash").GetString().Should().Be(Hash);
            body.RootElement.GetProperty("locationName").GetString().Should().Be("Stockholm HQ");
        }

        vm.WaitForDecisionCommand.Execute(null);
        await WaitUntilAsync(() => vm.Stage == AutopilotFlowStage.Finished);

        vm.RequestState.Should().Be(AutopilotRegistrationState.Imported);
        vm.IsPositiveOutcome.Should().BeTrue();
        vm.Detail.Should().Contain("EMEA-SE-STD");
        handler.Authorizations[$"GET /api/v1/autopilot/registrations/{requestId}"].Should().Be("Bearer status-token");
    }

    [Fact]
    public async Task Flow_ShowsTheServiceMessage_WhenSubmissionIsRefused()
    {
        StageTooling();
        var handler = new ScriptedHandler();
        handler.Respond("POST /api/v1/autopilot/registrations", HttpStatusCode.Forbidden, new { title = "Forbidden", status = 403, detail = "Autopilot registration is turned off in the portal." });
        var vm = new AutopilotRegistrationViewModel(
            new DeviceGatewayApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://gw.example.com") }),
            Capture(WritesReport),
            navigateBack: () => { },
            new SystemClockSynchronizationService(ntpServers: []),
            readIdentity: () => new DeviceIdentity("SN-1", "Contoso", "Laptop 7", null, null));

        vm.Start();
        await WaitUntilAsync(() => vm.Stage == AutopilotFlowStage.Failed);

        vm.ErrorMessage.Should().Contain("turned off");
        vm.RetryCommand.CanExecute(null).Should().BeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = new();

        public Dictionary<string, string> Bodies { get; } = new();
        public Dictionary<string, string?> Authorizations { get; } = new();

        public void Respond(string key, HttpStatusCode status, object body) =>
            _responses[key] = (status, JsonSerializer.Serialize(body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            Bodies[key] = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorizations[key] = request.Headers.Authorization?.ToString();
            if (!_responses.TryGetValue(key, out var response))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var mediaType = (int)response.Status >= 400 ? "application/problem+json" : "application/json";
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body, Encoding.UTF8, mediaType) };
        }
    }
}
