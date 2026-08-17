using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
}
