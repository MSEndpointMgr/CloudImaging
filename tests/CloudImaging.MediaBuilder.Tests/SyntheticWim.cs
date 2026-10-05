using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>Builds minimal WIM files (header + XML metadata only) for architecture-detection tests.</summary>
internal static class SyntheticWim
{
    public const int Amd64 = 9;
    public const int Arm64 = 12;
    public const int X86 = 0;

    public static byte[] Build(int? processorArchitecture, bool compressedXml = false)
    {
        var arch = processorArchitecture is { } value ? $"<ARCH>{value}</ARCH>" : string.Empty;
        var xml = $"<WIM><TOTALBYTES>1</TOTALBYTES><IMAGE INDEX=\"1\"><WINDOWS>{arch}</WINDOWS><NAME>Microsoft Windows PE</NAME></IMAGE></WIM>";
        var xmlBytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(xml)).ToArray();

        var header = new byte[208];
        "MSWIM\0\0\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 208);
        var flags = compressedXml ? 0x04UL : 0x02UL;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72), (flags << 56) | (uint)xmlBytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(80), 208);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(88), xmlBytes.Length);

        return header.Concat(xmlBytes).ToArray();
    }

    public static string Write(int? processorArchitecture)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ci-synthetic-{Guid.NewGuid():N}.wim");
        File.WriteAllBytes(path, Build(processorArchitecture));
        return path;
    }
}
