using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using FluentAssertions;
using Xunit;

namespace CloudImaging.ImagingCoreApi.Tests.Services.Drivers;

/// <summary>
/// The cabinet reader against the real vendor catalogs. Expected sizes and hashes come from
/// Windows expand.exe and are recorded in tests/fixtures/drivers/sources.json.
/// </summary>
public sealed class CabinetReaderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static TheoryData<string> FixtureNames()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in LoadSources())
        {
            data.Add(fixture.File);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void ExtractSingleFile_RealVendorCatalog_MatchesExpandOutputByteForByte(string fileName)
    {
        var expected = LoadSources().Single(source => source.File == fileName);
        using var output = new MemoryStream();

        var file = CabinetReader.ExtractSingleFile(ReadFixture(fileName), output);

        file.Size.Should().Be(expected.ContentBytes);
        output.Length.Should().Be(expected.ContentBytes);
        Convert.ToHexStringLower(SHA256.HashData(output.ToArray())).Should().Be(expected.ContentSha256);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void ExtractSingleFile_RealVendorCatalog_IsWellFormedXml(string fileName)
    {
        using var output = new MemoryStream();
        CabinetReader.ExtractSingleFile(ReadFixture(fileName), output);
        output.Position = 0;

        using var reader = XmlReader.Create(output, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var elements = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                elements++;
            }
        }

        elements.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData("platformList.cab", 3)]
    [InlineData("8c26_64_11.0.24h2.cab", 3)]
    [InlineData("CatalogIndexPC.cab", 1)]
    [InlineData("Latitude_0CB2.cab", 1)]
    public void Fixture_CompressionType_IsTheOneRecorded(string fileName, int compression)
    {
        var cabinet = ReadFixture(fileName);

        (ReadFolderCompression(cabinet) & 0x000F).Should().Be(compression);
    }

    [Fact]
    public void ExtractSingleFile_CabinetWithTwentyByteReserve_SkipsTheReserve()
    {
        var cabinet = ReadFixture("8c26_64_11.0.24h2.cab");
        (BinaryPrimitives.ReadUInt16LittleEndian(cabinet.AsSpan(30)) & 0x0004).Should().Be(0x0004);
        BinaryPrimitives.ReadUInt16LittleEndian(cabinet.AsSpan(36)).Should().Be(20);

        var act = () => CabinetReader.ExtractSingleFile(cabinet, Stream.Null);

        act.Should().NotThrow();
    }

    [Fact]
    public void ExtractSingleFile_MsZipWithManyBlocks_CarriesHistoryAcrossBlocks()
    {
        var cabinet = ReadFixture("Latitude_0CB2.cab");
        BlockCount(cabinet).Should().BeGreaterThan(100);
        using var output = new MemoryStream();

        CabinetReader.ExtractSingleFile(cabinet, output);

        var expected = LoadSources().Single(source => source.File == "Latitude_0CB2.cab");
        Convert.ToHexStringLower(SHA256.HashData(output.ToArray())).Should().Be(expected.ContentSha256);
    }

    [Fact]
    public async Task ExtractSingleFileAsync_Stream_WritesTheSameContent()
    {
        var cabinet = ReadFixture("platformList.cab");
        using var input = new MemoryStream(cabinet);
        using var output = new MemoryStream();

        var file = await CabinetReader.ExtractSingleFileAsync(input, output);

        file.Name.Should().NotBeNullOrWhiteSpace();
        output.Length.Should().Be(file.Size);
    }

    [Fact]
    public void ExtractSingleFile_TruncatedCabinet_ThrowsWithReason()
    {
        var cabinet = ReadFixture("Latitude_0C85.cab");
        var truncated = cabinet.AsSpan(0, cabinet.Length / 2).ToArray();

        var act = () => CabinetReader.ExtractSingleFile(truncated, Stream.Null);

        act.Should().Throw<InvalidDataException>().WithMessage("*truncated*");
    }

    [Fact]
    public void ExtractSingleFile_WrongSignature_ThrowsWithReason()
    {
        var cabinet = ReadFixture("platformList.cab");
        cabinet[0] = (byte)'X';

        var act = () => CabinetReader.ExtractSingleFile(cabinet, Stream.Null);

        act.Should().Throw<InvalidDataException>().WithMessage("*MSCF signature*");
    }

    [Fact]
    public void ExtractSingleFile_TwoFolders_ThrowsWithReason()
    {
        var cabinet = ReadFixture("platformList.cab");
        BinaryPrimitives.WriteUInt16LittleEndian(cabinet.AsSpan(26), 2);

        var act = () => CabinetReader.ExtractSingleFile(cabinet, Stream.Null);

        act.Should().Throw<InvalidDataException>().WithMessage("*one folder*");
    }

    [Fact]
    public void ExtractSingleFile_QuantumCompression_ThrowsWithReason()
    {
        var cabinet = ReadFixture("CatalogIndexPC.cab");
        BinaryPrimitives.WriteUInt16LittleEndian(cabinet.AsSpan(FolderOffset(cabinet) + 6), 2);

        var act = () => CabinetReader.ExtractSingleFile(cabinet, Stream.Null);

        act.Should().Throw<InvalidDataException>().WithMessage("*compression type 2*");
    }

    [Fact]
    public void ExtractSingleFile_CorruptedCompressedData_ThrowsInvalidData()
    {
        var cabinet = ReadFixture("8cbe_arm64_11.0.24h2.cab");
        var firstData = (int)BinaryPrimitives.ReadUInt32LittleEndian(cabinet.AsSpan(FolderOffset(cabinet)));
        for (var i = firstData + 8; i < firstData + 400; i++)
        {
            cabinet[i] ^= 0x5A;
        }

        var act = () => CabinetReader.ExtractSingleFile(cabinet, Stream.Null);

        act.Should().Throw<InvalidDataException>();
    }

    internal static byte[] ReadFixture(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "drivers", fileName));

    private static int FolderOffset(byte[] cabinet) => 40 + BinaryPrimitives.ReadUInt16LittleEndian(cabinet.AsSpan(36));

    private static int ReadFolderCompression(byte[] cabinet) => BinaryPrimitives.ReadUInt16LittleEndian(cabinet.AsSpan(FolderOffset(cabinet) + 6));

    private static int BlockCount(byte[] cabinet) => BinaryPrimitives.ReadUInt16LittleEndian(cabinet.AsSpan(FolderOffset(cabinet) + 4));

    private static IReadOnlyList<FixtureSource> LoadSources()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "drivers", "sources.json"));
        var document = JsonSerializer.Deserialize<FixtureSources>(json, JsonOptions);
        return document?.Files ?? [];
    }

    private sealed record FixtureSources(IReadOnlyList<FixtureSource> Files);

    private sealed record FixtureSource(string File, string Compression, long ContentBytes, string ContentSha256);
}
