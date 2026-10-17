#if DEV_SIMULATION
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CloudImaging.Client.Services;

namespace CloudImaging.Client.DevMode;

/// <summary>
/// DEV-ONLY stand-ins for "Register with Autopilot": a scripted Device Gateway and a fake OA3Tool,
/// so every Autopilot screen can be walked through on a workstation without WinPE, a TPM, the
/// boot-media certificate or a reachable gateway. Compiled only in Debug (DEV_SIMULATION).
/// </summary>
internal static class DevAutopilotSimulation
{
    public static readonly TimeSpan PollInterval = ViewModels.AutopilotRegistrationViewModel.StatusPollInterval;

    /// <summary>A gateway whose status endpoint walks through <paramref name="statusSequence"/>, one state per poll.</summary>
    public static DeviceGatewayApiClient Gateway(string[] statusSequence, string? rejectionReason = null, string? refusal = null) =>
        new(new HttpClient(new ScriptedGateway(statusSequence, rejectionReason, refusal)) { BaseAddress = new Uri("https://dev-simulation.invalid") });

    /// <summary>A capture service backed by a fake OA3Tool that returns a hash with or without TPM 2.0 data.</summary>
    public static AutopilotHashCaptureService Capture(bool withTpmData)
    {
        var root = Path.Combine(Path.GetTempPath(), "ci-dev-autopilot");
        var toolDir = Path.Combine(root, "client", AutopilotHashCaptureService.ToolRelativeDirectory);
        var systemDir = Path.Combine(root, "system32");
        Directory.CreateDirectory(toolDir);
        Directory.CreateDirectory(systemDir);
        File.WriteAllText(Path.Combine(toolDir, AutopilotHashCaptureService.Oa3ToolFileName), "dev simulation stub");
        File.WriteAllText(Path.Combine(systemDir, AutopilotHashCaptureService.TpmProviderFileName), "dev simulation stub");

        var hash = SyntheticHash(withTpmData);
        return new AutopilotHashCaptureService(
            clientDirectory: Path.Combine(root, "client"),
            systemDirectory: systemDir,
            runProcessOverride: async (file, _, workingDirectory, _, ct) =>
            {
                if (file.EndsWith(AutopilotHashCaptureService.Oa3ToolFileName, StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    await File.WriteAllTextAsync(Path.Combine(workingDirectory, "OA3.xml"), $"<Key><HardwareHash>{hash}</HardwareHash></Key>", ct);
                }
                return (0, "dev simulation");
            });
    }

    /// <summary>A hash in the 4K record layout: TPM 2.0 always, the endorsement key only when <paramref name="withTpmData"/>.</summary>
    private static string SyntheticHash(bool withTpmData)
    {
        var records = new List<(ushort Type, byte[] Value)> { (1, new byte[24]), (13, "2.0\0"u8.ToArray()) };
        if (withTpmData) records.Add((25, [0x01, 0x02, 0x03, 0x04]));
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

    private sealed class ScriptedGateway(string[] statusSequence, string? rejectionReason, string? refusal) : HttpMessageHandler
    {
        private readonly Guid _requestId = Guid.NewGuid();
        private int _polls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Long enough for the busy states to be seen.
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken);
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == "/api/v1/autopilot/availability")
                return Json(HttpStatusCode.OK, new { enabled = true });

            if (request.Method == HttpMethod.Post && path == "/api/v1/autopilot/registrations")
            {
                return refusal is not null
                    ? Json(HttpStatusCode.Forbidden, new { title = "Forbidden", status = 403, detail = refusal }, "application/problem+json")
                    : Json(HttpStatusCode.Created, new { requestId = _requestId, referenceCode = "AP-DEV42", state = "PendingApproval", statusToken = "dev-status-token", expiresAt = DateTimeOffset.UtcNow.AddDays(7) });
            }

            if (request.Method == HttpMethod.Get && path == $"/api/v1/autopilot/registrations/{_requestId}")
            {
                var state = statusSequence[Math.Min(_polls++, statusSequence.Length - 1)];
                return Json(HttpStatusCode.OK, new
                {
                    requestId = _requestId,
                    referenceCode = "AP-DEV42",
                    state,
                    groupTag = state == "Imported" ? "EMEA-SE-STD" : null,
                    rejectionReason = state == "Rejected" ? rejectionReason : null,
                    updatedAt = DateTimeOffset.UtcNow,
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object body, string mediaType = "application/json") =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, mediaType) };
    }
}
#endif
