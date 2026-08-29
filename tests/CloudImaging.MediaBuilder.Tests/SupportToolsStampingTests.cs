using System.IO;
using System.Text.Json;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Tests for stamping the "Enable command prompt access" opt-in (FR-051d) into the Client's
/// appsettings.json during boot image generation — the Media Builder side of the Command
/// Prompt support tool feature (see <see cref="BootImageGenerationService.StampSupportToolsConfigAsync"/>).
/// </summary>
public sealed class SupportToolsStampingTests
{
    [Fact]
    public async Task StampSupportToolsConfigAsync_WritesTrue_IntoExistingSection()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var appSettingsPath = Path.Combine(dir.FullName, "appsettings.json");
            await File.WriteAllTextAsync(appSettingsPath,
                """{ "SupportTools": { "CommandPromptEnabled": false } }""");

            await BootImageGenerationService.StampSupportToolsConfigAsync(
                dir.FullName, enableCommandPromptAccess: true, CancellationToken.None);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
            doc.RootElement.GetProperty("SupportTools").GetProperty("CommandPromptEnabled").GetBoolean()
                .Should().BeTrue();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StampSupportToolsConfigAsync_AddsSection_WhenMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var appSettingsPath = Path.Combine(dir.FullName, "appsettings.json");
            await File.WriteAllTextAsync(appSettingsPath, """{ "SomeOtherSetting": "kept" }""");

            await BootImageGenerationService.StampSupportToolsConfigAsync(
                dir.FullName, enableCommandPromptAccess: true, CancellationToken.None);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
            doc.RootElement.GetProperty("SupportTools").GetProperty("CommandPromptEnabled").GetBoolean()
                .Should().BeTrue();
            doc.RootElement.GetProperty("SomeOtherSetting").GetString()
                .Should().Be("kept", "existing settings must not be clobbered");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StampSupportToolsConfigAsync_AlwaysWritesFalse_UnlikeUrlStamping_WhichSkipsEmptyValues()
    {
        // Unlike StampDeviceGatewayBaseUrlAsync (which no-ops on an empty URL), a bool has no
        // "nothing to write" state — the resolved checkbox value must always be written so the
        // result is deterministic regardless of what the source client binaries shipped with.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var appSettingsPath = Path.Combine(dir.FullName, "appsettings.json");
            await File.WriteAllTextAsync(appSettingsPath,
                """{ "SupportTools": { "CommandPromptEnabled": true } }""");

            await BootImageGenerationService.StampSupportToolsConfigAsync(
                dir.FullName, enableCommandPromptAccess: false, CancellationToken.None);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(appSettingsPath));
            doc.RootElement.GetProperty("SupportTools").GetProperty("CommandPromptEnabled").GetBoolean()
                .Should().BeFalse();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StampSupportToolsConfigAsync_NoOp_WhenAppSettingsMissing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var act = async () => await BootImageGenerationService.StampSupportToolsConfigAsync(
                dir.FullName, enableCommandPromptAccess: true, CancellationToken.None);

            await act.Should().NotThrowAsync();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
