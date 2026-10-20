using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Xml;

namespace CloudImaging.Contracts.Models;

/// <summary>
/// Reads a WIM's processor architecture from its XML metadata resource without DISM, so it
/// works unelevated (Media Builder) and on Linux (Imaging Core). Only the fixed header and the
/// XML resource are read, never the image payload. The Portal has a TypeScript twin of this
/// parser (wimMetadata.ts); keep both in sync.
/// </summary>
public static class WimMetadataReader
{
    /// <summary>Size of the fixed WIM header (WIMHEADER_V1_PACKED).</summary>
    public const int HeaderSize = 208;

    /// <summary>XML resources larger than this are treated as unreadable rather than buffered.</summary>
    public const long MaxXmlDataBytes = 16L * 1024 * 1024;

    private const int XmlDataResourceOffset = 72;
    private const byte CompressedResourceFlag = 0x04;
    private const int ProcessorArchitectureAmd64 = 9;
    private const int ProcessorArchitectureArm64 = 12;

    private static ReadOnlySpan<byte> WimMagic => "MSWIM\0\0\0"u8;

    /// <summary>
    /// Locates the XML metadata resource from the first <see cref="HeaderSize"/> bytes. False when
    /// the header is not a WIM, the XML resource is compressed/absent, or it is implausibly large.
    /// </summary>
    public static bool TryGetXmlDataRange(ReadOnlySpan<byte> header, out long offset, out long length)
    {
        offset = 0;
        length = 0;
        if (header.Length < HeaderSize || !header[..WimMagic.Length].SequenceEqual(WimMagic))
            return false;

        var resource = header.Slice(XmlDataResourceOffset, 24);
        var sizeAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(resource);
        var flags = (byte)(sizeAndFlags >> 56);
        var size = (long)(sizeAndFlags & 0x00FF_FFFF_FFFF_FFFFUL);
        var resourceOffset = BinaryPrimitives.ReadInt64LittleEndian(resource[8..]);

        if ((flags & CompressedResourceFlag) != 0 || size <= 0 || size > MaxXmlDataBytes || resourceOffset < HeaderSize)
            return false;

        offset = resourceOffset;
        length = size;
        return true;
    }

    /// <summary>
    /// Returns the raw PROCESSOR_ARCHITECTURE value (<c>IMAGE/WINDOWS/ARCH</c>) of image
    /// <paramref name="imageIndex"/>, or null when the XML carries none.
    /// </summary>
    public static int? ParseProcessorArchitecture(ReadOnlySpan<byte> xmlData, int imageIndex = 1)
    {
        var arch = LoadXml(xmlData)?.SelectSingleNode($"/WIM/IMAGE[@INDEX='{imageIndex}']/WINDOWS/ARCH")?.InnerText;
        return int.TryParse(arch, out var value) ? value : null;
    }

    /// <summary>Maps a raw PROCESSOR_ARCHITECTURE value to a supported architecture; null for x86/other.</summary>
    public static MachineArchitecture? ToMachineArchitecture(int processorArchitecture) => processorArchitecture switch
    {
        ProcessorArchitectureAmd64 => MachineArchitecture.X64,
        ProcessorArchitectureArm64 => MachineArchitecture.Arm64,
        _ => null,
    };

    /// <summary>
    /// Whether image <paramref name="imageIndex"/> is built on Windows RE. Both bases report the
    /// WindowsPE edition, so the image name is what tells them apart: Windows RE keeps "Microsoft
    /// Windows Recovery Environment" through mount and commit, ADK images say "Microsoft Windows PE".
    /// </summary>
    public static BootImageBase ParseBootImageBase(ReadOnlySpan<byte> xmlData, int imageIndex = 1)
    {
        var document = LoadXml(xmlData);
        var image = document?.SelectSingleNode($"/WIM/IMAGE[@INDEX='{imageIndex}']");
        var text = $"{image?.SelectSingleNode("NAME")?.InnerText} {image?.SelectSingleNode("DESCRIPTION")?.InnerText}";
        return text.Contains("Recovery Environment", StringComparison.OrdinalIgnoreCase) ? BootImageBase.WindowsRE : BootImageBase.WindowsPE;
    }

    /// <summary>Reads the base of image 1 from a WIM file; Windows PE when it cannot be determined.</summary>
    public static async Task<BootImageBase> ReadBootImageBaseAsync(string wimPath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(wimPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var xml = await ReadXmlDataAsync(stream, ct);
        return xml is null ? BootImageBase.WindowsPE : ParseBootImageBase(xml);
    }

    /// <summary>Architecture, build and default language of image <paramref name="imageIndex"/>; null when the XML is unreadable.</summary>
    public static WimImageInfo? ParseImageInfo(ReadOnlySpan<byte> xmlData, int imageIndex = 1)
    {
        var image = LoadXml(xmlData)?.SelectSingleNode($"/WIM/IMAGE[@INDEX='{imageIndex}']");
        if (image is null)
            return null;

        static int? Number(string? text) => int.TryParse(text, out var value) ? value : null;
        return new WimImageInfo(
            Number(image.SelectSingleNode("WINDOWS/ARCH")?.InnerText),
            Number(image.SelectSingleNode("WINDOWS/VERSION/BUILD")?.InnerText),
            Number(image.SelectSingleNode("WINDOWS/VERSION/SPBUILD")?.InnerText),
            image.SelectSingleNode("WINDOWS/LANGUAGES/DEFAULT")?.InnerText);
    }

    /// <summary>Reads <see cref="ParseImageInfo"/> for image 1 of a WIM file; null when it is not a readable WIM.</summary>
    public static async Task<WimImageInfo?> ReadImageInfoAsync(string wimPath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(wimPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var xml = await ReadXmlDataAsync(stream, ct);
        return xml is null ? null : ParseImageInfo(xml);
    }

    private static XmlDocument? LoadXml(ReadOnlySpan<byte> xmlData)
    {
        var text = DecodeXml(xmlData);
        if (text is null)
            return null;

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var document = new XmlDocument { XmlResolver = null };
            document.Load(reader);
            return document;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadXmlDataAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        stream.Seek(0, SeekOrigin.Begin);
        if (await ReadFullyAsync(stream, header, ct) < HeaderSize)
            return null;

        if (!TryGetXmlDataRange(header, out var offset, out var length) || offset + length > stream.Length)
            return null;

        var xml = new byte[length];
        stream.Seek(offset, SeekOrigin.Begin);
        return await ReadFullyAsync(stream, xml, ct) < length ? null : xml;
    }

    /// <summary>Reads the raw architecture of image 1 from a seekable WIM stream; null when undeterminable.</summary>
    public static async Task<int?> ReadProcessorArchitectureAsync(Stream stream, CancellationToken ct = default)
    {
        var xml = await ReadXmlDataAsync(stream, ct);
        return xml is null ? null : ParseProcessorArchitecture(xml);
    }

    /// <summary>File-path convenience over <see cref="ReadProcessorArchitectureAsync(Stream, CancellationToken)"/>.</summary>
    public static async Task<int?> ReadProcessorArchitectureAsync(string wimPath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(wimPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await ReadProcessorArchitectureAsync(stream, ct);
    }

    /// <summary>Display name for a raw PROCESSOR_ARCHITECTURE value, used in mismatch messages.</summary>
    public static string DescribeProcessorArchitecture(int processorArchitecture) => processorArchitecture switch
    {
        0 => "x86",
        5 => "ARM (32-bit)",
        ProcessorArchitectureAmd64 => "x64",
        ProcessorArchitectureArm64 => "arm64",
        _ => $"unknown ({processorArchitecture})",
    };

    private static string? DecodeXml(ReadOnlySpan<byte> xmlData)
    {
        // WIM XML is UTF-16LE with a BOM; tolerate a missing BOM as well.
        if (xmlData.Length >= 2 && xmlData[0] == 0xFF && xmlData[1] == 0xFE)
            return Encoding.Unicode.GetString(xmlData[2..]);
        if (xmlData.Length >= 2 && xmlData[1] == 0x00)
            return Encoding.Unicode.GetString(xmlData);
        return xmlData.Length > 0 ? Encoding.UTF8.GetString(xmlData) : null;
    }

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}
