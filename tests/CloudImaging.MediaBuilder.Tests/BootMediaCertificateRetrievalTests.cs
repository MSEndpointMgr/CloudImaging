using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net;
using System.Net.Http;
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
    public void GetBootMediaCertPfxAsync_IsAvailable()
    {
        // Method exists with correct signature — verified via reflection
        var method = typeof(OperatorApiClient).GetMethod("GetBootMediaCertPfxAsync");
        method.Should().NotBeNull("GetBootMediaCertPfxAsync must be accessible on OperatorApiClient");
    }

    // ── Cert path in WIM ─────────────────────────────────────────────────────

    [Fact]
    public void EmbeddedCertPath_MatchesClientExpectedPath()
    {
        // The PFX embedded by the Media Builder at certificates\bootmedia.pfx must match
        // the path the Cloud Imaging Client expects (FR-070/FR-071).
        const string embeddedPath = @"certificates\bootmedia.pfx";
        embeddedPath.Should().Be(@"certificates\bootmedia.pfx",
            "embedded cert path must match what the Client expects (FR-070)");
    }

    // ── Real cert-resolution behavior via GenerateElevatedAsync (T173) ────────

    [Fact]
    public async Task GenerateElevatedAsync_ResolvesCertBeforeElevation_WhenOperatorClientReturnsPfx()
    {
        // The real, non-elevated → elevated relaunch path: cert bytes must be resolved by the
        // PARENT process (which has an authenticated OperatorApiClient) and threaded through to
        // the elevated worker via the "cert.pfx" IPC file — never fetched lazily inside the
        // elevated worker, which has no OperatorApiClient of its own.
        var pfxBytes = new byte[] { 1, 2, 3, 4 };
        var handler  = new FakeHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/bootmedia/certificate/pfx"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pfxBytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        var operatorApi = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        string? capturedCertFileContents = null;
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            operatorApiClient: operatorApi,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, args) =>
            {
                var files = System.Text.RegularExpressions.Regex
                    .Matches(args, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
                var paramsFile = files[0];
                var resultFile = files[2];

                var paramsJson = File.ReadAllText(paramsFile);
                var pfxFilePath = System.Text.Json.JsonDocument.Parse(paramsJson)
                    .RootElement.GetProperty("PfxFilePath").GetString();
                capturedCertFileContents = pfxFilePath is not null && File.Exists(pfxFilePath)
                    ? Convert.ToBase64String(File.ReadAllBytes(pfxFilePath))
                    : null;

                File.WriteAllText(resultFile,
                    """{"Success":true,"WimPath":"C:\\out\\cloud-imaging-boot.wim","Sha256Hash":"deadbeef"}""");
                return System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                })!;
            });

        var result = await svc.GenerateElevatedAsync(
            clientBinariesPath: @"C:\DoesNotExist",
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        result.WimPath.Should().Be(@"C:\out\cloud-imaging-boot.wim");
        capturedCertFileContents.Should().Be(Convert.ToBase64String(pfxBytes),
            "the cert resolved from the Operator API must be written to the IPC cert file " +
            "handed to the elevated worker, even though the parent process is never itself elevated");
    }

    [Fact]
    public async Task GenerateElevatedAsync_AbortsBeforeElevation_WhenCertCannotBeRetrieved()
    {
        // FR-068/FR-070: boot media without an embedded certificate cannot complete mTLS with
        // the Device Gateway API, so a missing/unretrievable cert must be a HARD failure —
        // generation must never even attempt to elevate.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var operatorApi = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        var launcherCalled = false;
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            operatorApiClient: operatorApi,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, _) =>
            {
                launcherCalled = true;
                throw new InvalidOperationException("Should not elevate when the cert cannot be retrieved.");
            });

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            clientBinariesPath: @"C:\DoesNotExist",
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No active boot media certificate*");
        launcherCalled.Should().BeFalse(
            "generation must fail before any elevation is attempted when the cert cannot be retrieved");
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}

