using CloudImaging.Client.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>
/// Regression cover for target-disk sizing. Win32_DiskDrive.Size is a CIM uint64, and the original
/// implementation accepted only the string form, so on any provider that returns a boxed ulong
/// (Hyper-V guests among them) formatting failed immediately with "Could not determine the size of
/// disk 0" before a single partition was created.
/// </summary>
public sealed class DiskSizeConversionTests
{
    [Fact]
    public void Accepts_BoxedUInt64_AsReturnedByMostProviders()
    {
        DiskFormatService.TryConvertDiskSizeBytes(137_438_953_472UL, out var bytes).Should().BeTrue();
        bytes.Should().Be(137_438_953_472UL);
    }

    [Fact]
    public void Accepts_DecimalString_AsReturnedByOtherProviders()
    {
        DiskFormatService.TryConvertDiskSizeBytes("137438953472", out var bytes).Should().BeTrue();
        bytes.Should().Be(137_438_953_472UL);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void Rejects_MissingOrUnparseableValues(object? raw)
    {
        DiskFormatService.TryConvertDiskSizeBytes(raw, out var bytes).Should().BeFalse();
        bytes.Should().Be(0UL);
    }

    [Fact]
    public void Rejects_ZeroSize_SoAnUnreadableDiskIsNeverPartitioned()
    {
        DiskFormatService.TryConvertDiskSizeBytes(0UL, out _).Should().BeFalse();
    }
}
