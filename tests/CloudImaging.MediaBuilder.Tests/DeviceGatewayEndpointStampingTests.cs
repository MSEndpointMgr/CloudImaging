using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Tests for stamping the Device Gateway URL (resolved from Operator API) into the Client's
/// appsettings.json during boot image generation, so every boot image self-heals against
/// Device Gateway redeploys/renames instead of relying on a manually maintained config file.
/// </summary>
[Collection(ElevatedIpcDirTestGroup.Name)]
public sealed class DeviceGatewayEndpointStampingTests
{
    [Fact]
    public async Task GetEndpointConfigurationAsync_ReturnsDeviceGatewayBaseUrl_FromOperatorApiResponse()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { DeviceGatewayApiBaseUrl = "https://mse-dev-ci-func-gateway.azurewebsites.net" }),
        });
        var svc = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        var result = await svc.GetEndpointConfigurationAsync();

        result.DeviceGatewayApiBaseUrl.Should().Be("https://mse-dev-ci-func-gateway.azurewebsites.net",
            "the resolved base URL must come from the Operator API response body, not be hardcoded");
    }

    [Fact]
    public async Task GetEndpointConfigurationAsync_Throws_WhenOperatorApiReturnsEmptyBody()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
        });
        var svc = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        Func<Task> act = async () => await svc.GetEndpointConfigurationAsync();

        await act.Should().ThrowAsync<InvalidOperationException>(
            "an empty configuration response must fail loudly rather than silently stamping an empty base URL");
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task StampDeviceGatewayBaseUrlAsync_WritesResolvedBaseUrl_IntoExistingSection()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var appSettingsPath = Path.Combine(dir.FullName, "appsettings.json");
            await File.WriteAllTextAsync(appSettingsPath,
                """{ "DeviceGatewayApi": { "BaseUrl": "" } }""");

            await BootImageGenerationService.StampDeviceGatewayBaseUrlAsync(
                dir.FullName, "https://mse-dev-ci-func-gateway.azurewebsites.net", CancellationToken.None);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
            doc.RootElement.GetProperty("DeviceGatewayApi").GetProperty("BaseUrl").GetString()
                .Should().Be("https://mse-dev-ci-func-gateway.azurewebsites.net");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StampDeviceGatewayBaseUrlAsync_AddsSection_WhenMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var appSettingsPath = Path.Combine(dir.FullName, "appsettings.json");
            await File.WriteAllTextAsync(appSettingsPath, """{ "SomeOtherSetting": "kept" }""");

            await BootImageGenerationService.StampDeviceGatewayBaseUrlAsync(
                dir.FullName, "https://example.azurewebsites.net", CancellationToken.None);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
            doc.RootElement.GetProperty("DeviceGatewayApi").GetProperty("BaseUrl").GetString()
                .Should().Be("https://example.azurewebsites.net");
            doc.RootElement.GetProperty("SomeOtherSetting").GetString()
                .Should().Be("kept", "existing settings must not be clobbered");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StampDeviceGatewayBaseUrlAsync_NoOp_WhenBaseUrlEmpty()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var appSettingsPath = Path.Combine(dir.FullName, "appsettings.json");
            const string original = """{ "DeviceGatewayApi": { "BaseUrl": "https://unchanged" } }""";
            await File.WriteAllTextAsync(appSettingsPath, original);

            await BootImageGenerationService.StampDeviceGatewayBaseUrlAsync(
                dir.FullName, baseUrl: "", CancellationToken.None);

            (await File.ReadAllTextAsync(appSettingsPath)).Should().Be(original);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StampDeviceGatewayBaseUrlAsync_NoOp_WhenAppSettingsMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            // No appsettings.json present — must not throw
            var act = async () => await BootImageGenerationService.StampDeviceGatewayBaseUrlAsync(
                dir.FullName, "https://example.azurewebsites.net", CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ── Real URL-resolution behavior via GenerateElevatedAsync ────

    [Fact]
    public async Task GenerateElevatedAsync_ResolvesDeviceGatewayUrlBeforeElevation_AndThreadsThroughToWorker()
    {
        // Same relaunch path as the boot media certificate (see BootMediaCertificateRetrievalTests):
        // the URL must be resolved by the PARENT process (which has an authenticated
        // OperatorApiClient) and threaded through to the elevated worker via params.json, never
        // fetched lazily inside the elevated worker, which has no OperatorApiClient of its own.
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/bootmedia/certificate/pfx" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) },
            "/api/configuration/endpoints" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { DeviceGatewayApiBaseUrl = "https://gateway.example.com" }),
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var operatorApi = new OperatorApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.com") },
            NullLogger<OperatorApiClient>.Instance);

        string? capturedBaseUrl = null;
        var svc = new BootImageGenerationService(
            NullLogger<BootImageGenerationService>.Instance,
            operatorApiClient: operatorApi,
            isElevatedOverride: () => false,
            startElevatedProcessOverride: (_, args) =>
            {
                var files = Regex.Matches(args, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
                var paramsFile = files[0];
                var resultFile = files[2];

                var paramsJson = File.ReadAllText(paramsFile);
                capturedBaseUrl = JsonDocument.Parse(paramsJson).RootElement.GetProperty("DeviceGatewayBaseUrl").GetString();

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
        capturedBaseUrl.Should().Be("https://gateway.example.com",
            "the URL resolved from the Operator API must be written to the IPC params file handed " +
            "to the elevated worker, even though the parent process is never itself elevated");
    }

    [Fact]
    public async Task GenerateElevatedAsync_AbortsBeforeElevation_WhenDeviceGatewayUrlCannotBeResolved()
    {
        // A boot image with no working Device Gateway URL configured is non-functional, so a
        // failed/empty resolution must be a HARD failure, generation must never even attempt to
        // elevate (matching the boot media certificate's fatal-failure behavior). The cert route
        // succeeds here so the Device Gateway resolution step is what's actually under test.
        var handler = new FakeHttpMessageHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/bootmedia/certificate/pfx" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
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
                throw new InvalidOperationException("Should not elevate when the Device Gateway URL cannot be resolved.");
            });

        Func<Task> act = async () => await svc.GenerateElevatedAsync(
            clientBinariesPath: @"C:\DoesNotExist",
            pfxBytes: null,
            outputDirectory: Path.GetTempPath());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Could not resolve the Device Gateway API URL*");
        launcherCalled.Should().BeFalse(
            "generation must fail before any elevation is attempted when the Device Gateway URL cannot be resolved");
    }
}
