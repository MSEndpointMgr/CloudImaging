using System.Buffers.Binary;
using System.Text;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>The file a cabinet held.</summary>
/// <param name="Name">File name stored in the cabinet.</param>
/// <param name="Size">Uncompressed size in bytes.</param>
public sealed record CabinetFile(string Name, long Size);

/// <summary>
/// Reads vendor catalog cabinets on any OS (research R3): one folder holding one file, compressed
/// with MSZIP or LZX, written from Microsoft's published formats ([MS-CAB], [MS-MCI], [MS-PATCH]).
/// Cabinets that span several files, hold several folders or files, or use Quantum are refused.
/// </summary>
public static class CabinetReader
{
    /// <summary>Largest cabinet read into memory. Vendor catalogs are well under 1 MB.</summary>
    public const int MaxCabinetBytes = 64 * 1024 * 1024;

    /// <summary>Largest file a cabinet may expand to.</summary>
    public const long MaxExpandedBytes = 512L * 1024 * 1024;

    private const int HeaderSize = 36;
    private const ushort FlagPreviousCabinet = 0x0001;
    private const ushort FlagNextCabinet = 0x0002;
    private const ushort FlagReservePresent = 0x0004;
    private const int CompressionNone = 0;
    private const int CompressionMsZip = 1;
    private const int CompressionLzx = 3;

    /// <summary>Reads a cabinet from <paramref name="cabinet"/> and writes its single file to <paramref name="output"/>.</summary>
    public static async Task<CabinetFile> ExtractSingleFileAsync(Stream cabinet, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cabinet);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await cabinet.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxCabinetBytes)
            {
                throw new InvalidDataException($"The cabinet is larger than {MaxCabinetBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return ExtractSingleFile(buffer.ToArray(), output);
    }

    /// <summary>Decodes <paramref name="cabinet"/> and writes its single file to <paramref name="output"/>.</summary>
    public static CabinetFile ExtractSingleFile(byte[] cabinet, Stream output)
    {
        ArgumentNullException.ThrowIfNull(cabinet);
        ArgumentNullException.ThrowIfNull(output);

        Require(cabinet, 0, HeaderSize);
        if (cabinet[0] != (byte)'M' || cabinet[1] != (byte)'S' || cabinet[2] != (byte)'C' || cabinet[3] != (byte)'F')
        {
            throw new InvalidDataException("The file is not a cabinet: the MSCF signature is missing.");
        }

        var declaredSize = ReadUInt32(cabinet, 8);
        if (declaredSize > cabinet.Length)
        {
            throw new InvalidDataException($"The cabinet is truncated: it declares {declaredSize} bytes but has {cabinet.Length}.");
        }

        var filesOffset = checked((int)ReadUInt32(cabinet, 16));
        var folderCount = ReadUInt16(cabinet, 26);
        var fileCount = ReadUInt16(cabinet, 28);
        var flags = ReadUInt16(cabinet, 30);
        if ((flags & (FlagPreviousCabinet | FlagNextCabinet)) != 0)
        {
            throw new InvalidDataException("Cabinets that span several files are not supported.");
        }

        if (folderCount != 1)
        {
            throw new InvalidDataException($"Only cabinets with one folder are supported; this one has {folderCount}.");
        }

        if (fileCount != 1)
        {
            throw new InvalidDataException($"Only cabinets with one file are supported; this one has {fileCount}.");
        }

        var position = HeaderSize;
        int folderReserve = 0, dataReserve = 0;
        if ((flags & FlagReservePresent) != 0)
        {
            Require(cabinet, HeaderSize, 4);
            var headerReserve = ReadUInt16(cabinet, 36);
            folderReserve = cabinet[38];
            dataReserve = cabinet[39];
            position = 40 + headerReserve;
        }

        Require(cabinet, position, 8 + folderReserve);
        var firstDataBlock = checked((int)ReadUInt32(cabinet, position));
        var blockCount = ReadUInt16(cabinet, position + 4);
        var compression = ReadUInt16(cabinet, position + 6);

        Require(cabinet, filesOffset, 17);
        var fileSize = ReadUInt32(cabinet, filesOffset);
        var folderStart = ReadUInt32(cabinet, filesOffset + 4);
        if (folderStart != 0)
        {
            throw new InvalidDataException("The cabinet's file does not start at the beginning of its folder.");
        }

        var name = ReadName(cabinet, filesOffset + 16);
        var blocks = ReadDataBlocks(cabinet, firstDataBlock, blockCount, dataReserve);

        // Catalogs are expanded in memory, so the declared size is bounded and checked before any decoding.
        if (fileSize > MaxExpandedBytes || blocks.Sum(b => (long)b.UncompressedSize) != fileSize)
        {
            throw new InvalidDataException($"The cabinet's file declares {fileSize} bytes, more than a catalog may expand to or than its blocks hold.");
        }

        long written = (compression & 0x000F) switch
        {
            CompressionNone => CopyStored(cabinet, blocks, output),
            CompressionMsZip => DecodeMsZip(cabinet, blocks, output),
            CompressionLzx => DecodeLzx(cabinet, blocks, (compression >> 8) & 0x1F, output),
            _ => throw new InvalidDataException($"Cabinet compression type {compression & 0x000F} is not supported (only none, MSZIP and LZX)."),
        };

        if (written != fileSize)
        {
            throw new InvalidDataException($"The cabinet's file decoded to {written} bytes but declares {fileSize}.");
        }

        return new CabinetFile(name, fileSize);
    }

    private static List<DataBlock> ReadDataBlocks(byte[] cabinet, int position, int count, int reserve)
    {
        var blocks = new List<DataBlock>(count);
        for (var i = 0; i < count; i++)
        {
            Require(cabinet, position, 8 + reserve);
            var compressedSize = ReadUInt16(cabinet, position + 4);
            var uncompressedSize = ReadUInt16(cabinet, position + 6);
            var dataOffset = position + 8 + reserve;
            Require(cabinet, dataOffset, compressedSize);
            blocks.Add(new DataBlock(dataOffset, compressedSize, uncompressedSize));
            position = dataOffset + compressedSize;
        }

        return blocks;
    }

    private static long CopyStored(byte[] cabinet, List<DataBlock> blocks, Stream output)
    {
        long written = 0;
        foreach (var block in blocks)
        {
            output.Write(cabinet, block.Offset, block.CompressedSize);
            written += block.CompressedSize;
        }

        return written;
    }

    private static long DecodeMsZip(byte[] cabinet, List<DataBlock> blocks, Stream output)
    {
        var decoder = new MsZipDecoder();
        var frame = new byte[LzxDecoder.FrameSize];
        long written = 0;
        foreach (var block in blocks)
        {
            var produced = decoder.DecodeBlock(cabinet, block.Offset, block.CompressedSize, frame, block.UncompressedSize);
            if (produced != block.UncompressedSize)
            {
                throw new InvalidDataException($"An MSZIP data block decoded to {produced} bytes but declares {block.UncompressedSize}.");
            }

            output.Write(frame, 0, produced);
            written += produced;
        }

        return written;
    }

    private static long DecodeLzx(byte[] cabinet, List<DataBlock> blocks, int windowBits, Stream output)
    {
        var input = new byte[blocks.Sum(block => block.CompressedSize)];
        var position = 0;
        foreach (var block in blocks)
        {
            Buffer.BlockCopy(cabinet, block.Offset, input, position, block.CompressedSize);
            position += block.CompressedSize;
        }

        var decoder = new LzxDecoder(windowBits, input);
        var frame = new byte[LzxDecoder.FrameSize];
        long written = 0;
        foreach (var block in blocks)
        {
            decoder.DecodeFrame(block.UncompressedSize, frame);
            output.Write(frame, 0, block.UncompressedSize);
            written += block.UncompressedSize;
        }

        return written;
    }

    private static string ReadName(byte[] cabinet, int offset)
    {
        var end = Array.IndexOf(cabinet, (byte)0, offset);
        if (end < 0)
        {
            throw new InvalidDataException("The cabinet is truncated: a file name is not terminated.");
        }

        return Encoding.UTF8.GetString(cabinet, offset, end - offset);
    }

    private static void Require(byte[] cabinet, int offset, int count)
    {
        if (offset < 0 || count < 0 || (long)offset + count > cabinet.Length)
        {
            throw new InvalidDataException("The cabinet is truncated.");
        }
    }

    private static uint ReadUInt32(byte[] cabinet, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(cabinet.AsSpan(offset, 4));

    private static ushort ReadUInt16(byte[] cabinet, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(cabinet.AsSpan(offset, 2));

    private readonly record struct DataBlock(int Offset, int CompressedSize, int UncompressedSize);
}
