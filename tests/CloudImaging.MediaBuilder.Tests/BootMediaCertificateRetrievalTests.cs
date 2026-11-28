using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

[Collection(ElevatedIpcDirTestGroup.Name)]
/// <summary>
/// Tests for boot media certificate PFX retrieval and boot image cert embedding (T170, T173, FR-068, FR-070).
/// </summary>
public sealed class BootMediaCertificateRetrievalTests
{
    // ── OperatorApiClient cert retrieval ──────────────────────────────────────

    [Fact]
    public void OperatorApiClient_CanBeInstantiated()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://example.com") };
        var svc  = new OperatorApiClient(http, NullLogger<OperatorApiClient>.Instance);
        svc.Should().NotBeNull();
    }

    [Fact]
    public async Task GetBootMediaCertPfxAsync_ReturnsPfxBytes_OnSuccessResponse()
    {
        var pfxBytes = new byte[] { 9, 8, 7, 6, 5 };
        var handler  = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pfxBytes) });
        var svc = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        var result = await svc.GetBootMediaCertPfxAsync();

        result.Should().BeEquivalentTo(pfxBytes, "the returned bytes must be exactly the PFX content from the Operator API response body");
    }

    [Fact]
    public async Task GetBootMediaCertPfxAsync_Throws_WhenOperatorApiReturnsNotFound()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        Func<Task> act = async () => await svc.GetBootMediaCertPfxAsync();

        await act.Should().ThrowAsync<HttpRequestException>(
            "a missing active certificate must surface as a failure rather than silently returning an empty/garbage PFX");
    }

    // ── Cert path in WIM ─────────────────────────────────────────────────────

    [Fact]
    public void EmbeddedCertPath_MatchesClientExpectedPath()
    {
        // The PFX embedded by the Media Builder at certificates\bootmedia.pfx (produced via
        // Path.Combine("certificates", "bootmedia.pfx") in BootImageGenerationService.cs) must
        // match the path CloudImaging.Client.Services.SessionStartupCoordinator.PfxRelativePath
        // expects (asserted independently in tests/CloudImaging.Client.Tests) — cross-checked
        // here as a literal contract since this test project does not reference Client (FR-070/FR-071).
        var embeddedPath = Path.Combine("certificates", "bootmedia.pfx");
        embeddedPath.Should().Be(@"certificates\bootmedia.pfx",
            "embedded cert path must match what the Client's SessionStartupCoordinator.PfxRelativePath expects (FR-070)");
    }

    // ── Real cert-resolution behavior via GenerateElevatedAsync (T173) ────────

    [Fact]
    public async Task GenerateElevatedAsync_ResolvesCertInTheParent_WhenOperatorClientReturnsPfx()
    {
        // The real, non-elevated → elevated relaunch path: cert bytes must be resolved by the
        // PARENT process (which has an authenticated OperatorApiClient) and threaded through to
        // the elevated worker via the "cert.pfx" IPC file — never fetched lazily inside the
        // elevated worker, which has no OperatorApiClient of its own.
        var pfxBytes = new byte[] { 1, 2, 3, 4 };
        var handler  = new FakeHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath switch
            {
                "/api/bootmedia/certificate/pfx" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pfxBytes) },
                "/api/configuration/endpoints" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { DeviceGatewayApiBaseUrl = "https://gateway.example.com" }),
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        var operatorApi = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        string? capturedCertFileContents = null;
        var worker = new FakeElevatedWorker(onParams: paramsJson =>
        {
            var pfxFilePath = System.Text.Json.JsonDocument.Parse(paramsJson)
                .RootElement.GetProperty("PfxFilePath").GetString();
            capturedCertFileContents = pfxFilePath is not null && File.Exists(pfxFilePath)
                ? Convert.ToBase64String(File.ReadAllBytes(pfxFilePath))
                : null;
        });
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            operatorApiClient: operatorApi,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        var result = await svc.GenerateElevatedAsync(
            FakeElevatedWorker.Inputs(),
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        result.WimPath.Should().Be(@"C:\out\cloud-imaging-boot.wim");
        capturedCertFileContents.Should().Be(Convert.ToBase64String(pfxBytes),
            "the cert resolved from the Operator API must be written to the IPC cert file " +
            "handed to the elevated worker, even though the parent process is never itself elevated");
    }

    [Fact]
    public async Task GenerateElevatedAsync_StopsTheWorkerBeforeAnyDownload_WhenCertCannotBeRetrieved()
    {
        // FR-068/FR-070: boot media without an embedded certificate cannot complete mTLS with
        // the Device Gateway API, so a missing/unretrievable cert must be a HARD failure. The
        // UAC prompt comes first, so the waiting worker is stopped and nothing is downloaded.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var operatorApi = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        var prepared = false;
        var worker = new FakeElevatedWorker();
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            operatorApiClient: operatorApi,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: worker.Start);

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            _ =>
            {
                prepared = true;
                return Task.FromResult(new BootImageGenerationService.GenerationInputs(@"C:\DoesNotExist"));
            },
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active boot media certificate*");
        prepared.Should().BeFalse("the certificate is checked before anything is downloaded");
        worker.WasStopped.Should().BeTrue("the waiting worker must not outlive the failed run");
        worker.ReceivedParams.Should().BeFalse();
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}

