using System.Text;
using CloudImaging.ImagingCoreApi.Services;
using DiscUtils.Iso9660;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services;

/// <summary>
/// Unit tests for <see cref="IsoExtractionService"/> — closes the ISO-support gap: the Client's
/// DISM apply step can only ever apply a genuine WIM/ESD, so an uploaded OS image ISO must be
/// extracted to a real WIM/ESD once at publish time. These tests build small in-memory ISO9660
/// images (via <see cref="CDBuilder"/>) rather than depending on a real multi-GB Windows ISO.
/// </summary>
public sealed class IsoExtractionServiceTests
{
    private static IsoExtractionService CreateService() =>
        new(NullLogger<IsoExtractionService>.Instance);

    private static MemoryStream BuildIso(string? entryPath, byte[]? entryContent)
    {
        var builder = new CDBuilder { UseJoliet = true };
        if (entryPath is not null)
        {
            builder.AddFile(entryPath, entryContent!);
        }

        var built = new MemoryStream();
        using (var buildStream = builder.Build())
        {
            buildStream.CopyTo(built);
        }

        built.Position = 0;
        return built;
    }

    [Fact]
    public async Task ExtractInstallImageAsync_FindsInstallWim_WhenPresent()
    {
        var expected = Encoding.UTF8.GetBytes("fake install.wim contents");
        await using var iso = BuildIso(@"sources\install.wim", expected);
        await using var destination = new MemoryStream();

        var result = await CreateService().ExtractInstallImageAsync(iso, destination, CancellationToken.None);

        result.Found.Should().BeTrue("the ISO contains sources\\install.wim");
        result.SourcePath.Should().Be(@"\sources\install.wim");
        result.Extension.Should().Be(".wim");
        destination.ToArray().Should().Equal(expected, "the extracted bytes must match the source file exactly");
    }

    [Fact]
    public async Task ExtractInstallImageAsync_FallsBackToInstallEsd_WhenWimAbsent()
    {
        var expected = Encoding.UTF8.GetBytes("fake install.esd contents");
        await using var iso = BuildIso(@"sources\install.esd", expected);
        await using var destination = new MemoryStream();

        var result = await CreateService().ExtractInstallImageAsync(iso, destination, CancellationToken.None);

        result.Found.Should().BeTrue("install.esd is a valid fallback when install.wim is absent");
        result.SourcePath.Should().Be(@"\sources\install.esd");
        result.Extension.Should().Be(".esd");
        destination.ToArray().Should().Equal(expected);
    }

    [Fact]
    public async Task ExtractInstallImageAsync_ReturnsNotFound_WhenNeitherWimNorEsdPresent()
    {
        await using var iso = BuildIso("readme.txt", Encoding.UTF8.GetBytes("not an install image"));
        await using var destination = new MemoryStream();

        var result = await CreateService().ExtractInstallImageAsync(iso, destination, CancellationToken.None);

        result.Found.Should().BeFalse("neither sources\\install.wim nor sources\\install.esd is present");
        result.FailureReason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ExtractInstallImageAsync_ReturnsFailure_ForGarbageStream()
    {
        var garbage = Encoding.UTF8.GetBytes("this is definitely not an ISO9660/UDF image");
        await using var notAnIso = new MemoryStream(garbage);
        await using var destination = new MemoryStream();

        var result = await CreateService().ExtractInstallImageAsync(notAnIso, destination, CancellationToken.None);

        result.Found.Should().BeFalse("a non-ISO stream cannot be opened as a disc image");
        result.FailureReason.Should().NotBeNullOrEmpty();
    }
}
