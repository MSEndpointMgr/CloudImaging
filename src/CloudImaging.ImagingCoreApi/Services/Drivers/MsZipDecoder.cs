namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// MSZIP decoder ([MS-MCI]): every cabinet data block starts with "CK" and holds a complete
/// deflate stream (RFC 1951), but back references may reach into the previous blocks, so the
/// 32 KB history window is carried from block to block. <see cref="System.IO.Compression.DeflateStream"/>
/// cannot be given a preset history, hence this inflater.
/// </summary>
internal sealed class MsZipDecoder
{
    private const int WindowSize = 32768;
    private const int WindowMask = WindowSize - 1;

    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly byte[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    private static readonly HuffmanTable FixedLiterals = BuildFixedLiterals();
    private static readonly HuffmanTable FixedDistances = new(Enumerable.Repeat((byte)5, 30).ToArray(), 15);

    private readonly byte[] _window = new byte[WindowSize];
    private int _windowPos;
    private long _produced;
    private DeflateBits _bits;
    private byte[] _output = [];
    private int _capacity;
    private int _outputCount;

    /// <summary>Decodes one cabinet data block into the first <paramref name="capacity"/> bytes of <paramref name="output"/> and returns the bytes written.</summary>
    public int DecodeBlock(byte[] data, int offset, int count, byte[] output, int capacity)
    {
        if (count < 2 || data[offset] != (byte)'C' || data[offset + 1] != (byte)'K')
        {
            throw new InvalidDataException("An MSZIP data block does not start with the CK signature.");
        }

        _bits = new DeflateBits(data, offset + 2, offset + count);
        _output = output;
        _capacity = Math.Min(capacity, output.Length);
        _outputCount = 0;

        int last;
        do
        {
            last = _bits.Read(1);
            switch (_bits.Read(2))
            {
                case 0:
                    CopyStored();
                    break;
                case 1:
                    DecodeCodes(FixedLiterals, FixedDistances);
                    break;
                case 2:
                    DecodeDynamic();
                    break;
                default:
                    throw new InvalidDataException("An MSZIP data block uses the reserved deflate block type.");
            }
        }
        while (last == 0);

        return _outputCount;
    }

    private static HuffmanTable BuildFixedLiterals()
    {
        var lengths = new byte[288];
        for (var i = 0; i < 144; i++)
        {
            lengths[i] = 8;
        }

        for (var i = 144; i < 256; i++)
        {
            lengths[i] = 9;
        }

        for (var i = 256; i < 280; i++)
        {
            lengths[i] = 7;
        }

        for (var i = 280; i < 288; i++)
        {
            lengths[i] = 8;
        }

        return new HuffmanTable(lengths, 15);
    }

    private void CopyStored()
    {
        _bits.AlignToByte();
        var length = _bits.ReadByte() | (_bits.ReadByte() << 8);
        var complement = _bits.ReadByte() | (_bits.ReadByte() << 8);
        if (length != (~complement & 0xFFFF))
        {
            throw new InvalidDataException("A stored deflate block has an invalid length.");
        }

        for (var i = 0; i < length; i++)
        {
            Put((byte)_bits.ReadByte());
        }
    }

    private void DecodeDynamic()
    {
        var literalCount = _bits.Read(5) + 257;
        var distanceCount = _bits.Read(5) + 1;
        var codeLengthCount = _bits.Read(4) + 4;
        if (literalCount > 286 || distanceCount > 30)
        {
            throw new InvalidDataException("A dynamic deflate block declares too many codes.");
        }

        var codeLengthLengths = new byte[19];
        for (var i = 0; i < codeLengthCount; i++)
        {
            codeLengthLengths[CodeLengthOrder[i]] = (byte)_bits.Read(3);
        }

        var codeLengthTable = new HuffmanTable(codeLengthLengths, 7);
        var lengths = new byte[literalCount + distanceCount];
        var index = 0;
        while (index < lengths.Length)
        {
            var symbol = codeLengthTable.Decode(ref _bits);
            if (symbol < 16)
            {
                lengths[index++] = (byte)symbol;
                continue;
            }

            byte value = 0;
            int repeat;
            if (symbol == 16)
            {
                if (index == 0)
                {
                    throw new InvalidDataException("A dynamic deflate block repeats a length before the first one.");
                }

                value = lengths[index - 1];
                repeat = 3 + _bits.Read(2);
            }
            else if (symbol == 17)
            {
                repeat = 3 + _bits.Read(3);
            }
            else
            {
                repeat = 11 + _bits.Read(7);
            }

            if (index + repeat > lengths.Length)
            {
                throw new InvalidDataException("A dynamic deflate block has too many code lengths.");
            }

            while (repeat-- > 0)
            {
                lengths[index++] = value;
            }
        }

        if (lengths[256] == 0)
        {
            throw new InvalidDataException("A dynamic deflate block has no end-of-block code.");
        }

        var literals = new HuffmanTable(lengths.AsSpan(0, literalCount), 15);
        var distances = new HuffmanTable(lengths.AsSpan(literalCount, distanceCount), 15);
        DecodeCodes(literals, distances);
    }

    private void DecodeCodes(HuffmanTable literals, HuffmanTable distances)
    {
        while (true)
        {
            var symbol = literals.Decode(ref _bits);
            if (symbol < 256)
            {
                Put((byte)symbol);
                continue;
            }

            if (symbol == 256)
            {
                return;
            }

            symbol -= 257;
            if (symbol >= LengthBase.Length)
            {
                throw new InvalidDataException("A deflate block uses an invalid length code.");
            }

            var length = LengthBase[symbol] + _bits.Read(LengthExtra[symbol]);
            var distanceSymbol = distances.Decode(ref _bits);
            if (distanceSymbol >= DistanceBase.Length)
            {
                throw new InvalidDataException("A deflate block uses an invalid distance code.");
            }

            var distance = DistanceBase[distanceSymbol] + _bits.Read(DistanceExtra[distanceSymbol]);
            if (distance > _produced)
            {
                throw new InvalidDataException("A deflate block refers to data before the start of the stream.");
            }

            var source = (_windowPos - distance) & WindowMask;
            for (var i = 0; i < length; i++)
            {
                Put(_window[source]);
                source = (source + 1) & WindowMask;
            }
        }
    }

    private void Put(byte value)
    {
        if (_outputCount >= _capacity)
        {
            throw new InvalidDataException("An MSZIP data block decodes to more bytes than it declares.");
        }

        _output[_outputCount++] = value;
        _window[_windowPos] = value;
        _windowPos = (_windowPos + 1) & WindowMask;
        _produced++;
    }

    /// <summary>Deflate bits: least significant bit of each byte first.</summary>
    private struct DeflateBits : IBitSource
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;
        private uint _buffer;
        private int _count;

        public DeflateBits(byte[] data, int position, int end)
        {
            _data = data;
            _position = position;
            _end = end;
            _buffer = 0;
            _count = 0;
        }

        public int NextBit()
        {
            if (_count == 0)
            {
                _buffer = (uint)ReadByte();
                _count = 8;
            }

            var bit = (int)(_buffer & 1);
            _buffer >>= 1;
            _count--;
            return bit;
        }

        public int Read(int bitCount)
        {
            var value = _buffer;
            while (_count < bitCount)
            {
                value |= (uint)ReadByte() << _count;
                _count += 8;
            }

            _buffer = value >> bitCount;
            _count -= bitCount;
            return (int)(value & ((1u << bitCount) - 1));
        }

        public void AlignToByte()
        {
            _buffer = 0;
            _count = 0;
        }

        public int ReadByte()
        {
            if (_position >= _end)
            {
                throw new InvalidDataException("An MSZIP data block is truncated.");
            }

            return _data[_position++];
        }
    }
}
