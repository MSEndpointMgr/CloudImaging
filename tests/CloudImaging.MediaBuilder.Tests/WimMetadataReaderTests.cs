using CloudImaging.Contracts.Models;
using FluentAssertions;
using System.IO;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>WIM XML-metadata architecture detection shared by Media Builder and Imaging Core.</summary>
public sealed class WimMetadataReaderTests
{
    [Theory]
    [InlineData(SyntheticWim.Amd64, MachineArchitecture.X64)]
    [InlineData(SyntheticWim.Arm64, MachineArchitecture.Arm64)]
    public async Task ReadsArchitectureFromXmlMetadata(int raw, MachineArchitecture expected)
    {
        using var stream = new MemoryStream(SyntheticWim.Build(raw));

        var detected = await WimMetadataReader.ReadProcessorArchitectureAsync(stream);

        detected.Should().Be(raw);
        WimMetadataReader.ToMachineArchitecture(detected!.Value).Should().Be(expected);
    }

    [Fact]
    public void UnsupportedArchitecture_MapsToNull()
    {
        WimMetadataReader.ToMachineArchitecture(SyntheticWim.X86).Should().BeNull();
        WimMetadataReader.DescribeProcessorArchitecture(SyntheticWim.X86).Should().Be("x86");
    }

    [Fact]
    public async Task ReturnsNull_WhenArchElementMissing()
    {
        using var stream = new MemoryStream(SyntheticWim.Build(null));

        (await WimMetadataReader.ReadProcessorArchitectureAsync(stream)).Should().BeNull();
    }

    [Fact]
    public void RejectsCompressedXmlResource()
    {
        var bytes = SyntheticWim.Build(SyntheticWim.Arm64, compressedXml: true);

        WimMetadataReader.TryGetXmlDataRange(bytes, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ReturnsNull_ForNonWimContent()
    {
        using var stream = new MemoryStream(new byte[512]);

        (await WimMetadataReader.ReadProcessorArchitectureAsync(stream)).Should().BeNull();
    }

    [Fact]
    public void RejectsXmlWithDtd()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE WIM [<!ENTITY x \"12\">]><WIM><IMAGE INDEX=\"1\"><WINDOWS><ARCH>&x;</ARCH></WINDOWS></IMAGE></WIM>"u8.ToArray();

        WimMetadataReader.ParseProcessorArchitecture(xml).Should().BeNull("DTD processing is prohibited");
    }
}
