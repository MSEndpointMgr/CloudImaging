using CloudImaging.Client.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Tests for the recovery-image apply fallback: reusing the OS image's own embedded
/// <c>Winre.wim</c> when no Recovery Image has been published to the Portal catalog, instead of
/// hard-failing the imaging session.
/// </summary>
public sealed class RecoveryImageServiceTests
{
    [Fact]
    public async Task ApplyFromEmbeddedImageAsync_ReturnsFalse_WhenNoEmbeddedWinreWimExists()
    {
        var service = new RecoveryImageService(NullLogger<RecoveryImageService>.Instance);

        // A fresh temp directory has no Windows\System32\Recovery\Winre.wim under it, so this
        // must report "nothing to fall back to" rather than throwing.
        var fakeWindowsVolume = Path.Combine(Path.GetTempPath(), $"ci-fake-windows-{Guid.NewGuid():N}");

        var result = await service.ApplyFromEmbeddedImageAsync(
            fakeWindowsVolume, recoveryVolume: fakeWindowsVolume, ct: CancellationToken.None);

        result.Should().BeFalse("no Winre.wim exists under the fake Windows volume root");
    }
}
