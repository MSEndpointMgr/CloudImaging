using System.IO;
using System.Text.Json;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
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
    public void GetEndpointConfigurationAsync_IsAvailable()
    {
        // Method exists with correct signature — verified via reflection
        var method = typeof(OperatorApiClient).GetMethod("GetEndpointConfigurationAsync");
        method.Should().NotBeNull("GetEndpointConfigurationAsync must be accessible on OperatorApiClient");
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
