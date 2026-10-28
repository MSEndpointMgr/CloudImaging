namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// LZX decoder for cabinet folders (the LZX format of [MS-PATCH], as used by MAKECAB). The input
/// is the concatenated payload of a folder's data blocks; output comes in frames of 32 KB (the
/// last one shorter), each matching one data block. The bitstream is read as 16-bit little-endian
/// words, most significant bit first, and is realigned to a 16-bit boundary after every frame.
/// </summary>
internal sealed class LzxDecoder
{
    public const int FrameSize = 32768;

    private const int MinMatch = 2;
    private const int NumChars = 256;
    private const int NumPrimaryLengths = 7;
    private const int NumSecondaryLengths = 249;
    private const int PretreeSize = 20;
    private const int AlignedSize = 8;
    private const int LengthTableSafety = 64;

    private const int BlockVerbatim = 1;
    private const int BlockAligned = 2;
    private const int BlockUncompressed = 3;

    private static readonly byte[] ExtraBits = BuildExtraBits();
    private static readonly int[] PositionBase = BuildPositionBase();

    private readonly byte[] _window;
    private readonly int _windowSize;
    private readonly int _mainSize;
    private readonly byte[] _mainLengths;
    private readonly byte[] _lengthLengths = new byte[NumSecondaryLengths + LengthTableSafety];
    private LzxBits _bits;

    private HuffmanTable? _mainTable;
    private HuffmanTable? _lengthTable;
    private HuffmanTable? _alignedTable;

    private uint _r0 = 1, _r1 = 1, _r2 = 1;
    private int _windowPos;
    private int _framePos;
    private int _blockType;
    private int _blockLength;
    private int _blockRemaining;
    private bool _headerRead;
    private bool _intelStarted;
    private int _intelFileSize;
    private long _offset;
    private int _frame;

    public LzxDecoder(int windowBits, byte[] input)
    {
        if (windowBits is < 15 or > 21)
        {
            throw new InvalidDataException($"The LZX window size of 2^{windowBits} bytes is not supported.");
        }

        _windowSize = 1 << windowBits;
        _window = new byte[_windowSize];
        var positionSlots = windowBits switch
        {
            20 => 42,
            21 => 50,
            _ => windowBits * 2,
        };
        _mainSize = NumChars + (positionSlots << 3);
        _mainLengths = new byte[_mainSize + LengthTableSafety];
        _bits = new LzxBits(input);
    }

    /// <summary>Decodes the next frame of <paramref name="frameSize"/> bytes into <paramref name="output"/>.</summary>
    public void DecodeFrame(int frameSize, Span<byte> output)
    {
        if (frameSize is <= 0 or > FrameSize)
        {
            throw new InvalidDataException($"An LZX frame of {frameSize} bytes is not valid.");
        }

        if (!_headerRead)
        {
            int high = 0, low = 0;
            if (_bits.Read(1) != 0)
            {
                high = _bits.Read(16);
                low = _bits.Read(16);
            }

            _intelFileSize = (high << 16) | low;
            _headerRead = true;
        }

        var bytesToDo = _framePos + frameSize - _windowPos;
        while (bytesToDo > 0)
        {
            if (_blockRemaining == 0)
            {
                ReadBlockHeader();
            }

            var thisRun = Math.Min(_blockRemaining, bytesToDo);
            bytesToDo -= thisRun;
            _blockRemaining -= thisRun;

            thisRun = _blockType == BlockUncompressed
                ? CopyUncompressed(thisRun)
                : DecodeMatches(thisRun, _blockType == BlockAligned);

            // The last match may run past this block's remaining length into the next block.
            if (thisRun < 0)
            {
                if (-thisRun > _blockRemaining)
                {
                    throw new InvalidDataException("An LZX match runs past the end of its block.");
                }

                _blockRemaining += thisRun;
            }
        }

        if (_windowPos - _framePos != frameSize)
        {
            throw new InvalidDataException("LZX data runs past the end of a frame.");
        }

        _bits.AlignToWord();

        var frame = output[..frameSize];
        _window.AsSpan(_framePos, frameSize).CopyTo(frame);
        if (_intelStarted && _intelFileSize != 0 && _frame < 32768 && frameSize > 10)
        {
            UndoIntelTranslation(frame);
        }

        _offset += frameSize;
        _framePos += frameSize;
        _frame++;
        if (_windowPos == _windowSize)
        {
            _windowPos = 0;
        }

        if (_framePos == _windowSize)
        {
            _framePos = 0;
        }
    }

    private static byte[] BuildExtraBits()
    {
        var extra = new byte[51];
        byte bits = 0;
        for (var i = 0; i < 50; i += 2)
        {
            extra[i] = extra[i + 1] = bits;
            if (i != 0 && bits < 17)
            {
                bits++;
            }
        }

        extra[50] = 17;
        return extra;
    }

    private static int[] BuildPositionBase()
    {
        var bases = new int[51];
        var position = 0;
        for (var i = 0; i < 51; i++)
        {
            bases[i] = position;
            position += 1 << ExtraBits[i];
        }

        return bases;
    }

    private void ReadBlockHeader()
    {
        // An odd-length uncompressed block is followed by one padding byte.
        if (_blockType == BlockUncompressed && (_blockLength & 1) != 0)
        {
            _bits.SkipByte();
        }

        _blockType = _bits.Read(3);
        var high = _bits.Read(16);
        var low = _bits.Read(8);
        _blockRemaining = _blockLength = (high << 8) | low;

        switch (_blockType)
        {
            case BlockAligned:
                var alignedLengths = new byte[AlignedSize];
                for (var i = 0; i < AlignedSize; i++)
                {
                    alignedLengths[i] = (byte)_bits.Read(3);
                }

                _alignedTable = new HuffmanTable(alignedLengths, 16);
                ReadMainAndLengthTrees();
                break;
            case BlockVerbatim:
                ReadMainAndLengthTrees();
                break;
            case BlockUncompressed:
                _intelStarted = true;
                _bits.AlignForUncompressed();
                _r0 = _bits.ReadRawUInt32();
                _r1 = _bits.ReadRawUInt32();
                _r2 = _bits.ReadRawUInt32();
                break;
            default:
                throw new InvalidDataException($"LZX block type {_blockType} is not valid.");
        }
    }

    private void ReadMainAndLengthTrees()
    {
        ReadLengths(_mainLengths, 0, NumChars);
        ReadLengths(_mainLengths, NumChars, _mainSize);
        _mainTable = new HuffmanTable(_mainLengths.AsSpan(0, _mainSize), 16);
        if (_mainLengths[0xE8] != 0)
        {
            _intelStarted = true;
        }

        ReadLengths(_lengthLengths, 0, NumSecondaryLengths);
        _lengthTable = new HuffmanTable(_lengthLengths.AsSpan(0, NumSecondaryLengths), 16);
    }

    /// <summary>Reads code lengths as deltas against the previous block's lengths, coded with a pretree.</summary>
    private void ReadLengths(byte[] lengths, int first, int last)
    {
        var pretreeLengths = new byte[PretreeSize];
        for (var i = 0; i < PretreeSize; i++)
        {
            pretreeLengths[i] = (byte)_bits.Read(4);
        }

        var pretree = new HuffmanTable(pretreeLengths, 16);
        var x = first;
        while (x < last)
        {
            var z = pretree.Decode(ref _bits);
            int run;
            switch (z)
            {
                case 17:
                    run = _bits.Read(4) + 4;
                    Fill(lengths, ref x, run, 0);
                    break;
                case 18:
                    run = _bits.Read(5) + 20;
                    Fill(lengths, ref x, run, 0);
                    break;
                case 19:
                    run = _bits.Read(1) + 4;
                    z = pretree.Decode(ref _bits);
                    if (z > 16)
                    {
                        throw new InvalidDataException("An LZX length run uses an invalid delta.");
                    }

                    Fill(lengths, ref x, run, Delta(lengths[x], z));
                    break;
                default:
                    lengths[x] = Delta(lengths[x], z);
                    x++;
                    break;
            }
        }
    }

    private static byte Delta(byte previous, int delta)
    {
        var value = previous - delta;
        return (byte)(value < 0 ? value + 17 : value);
    }

    private static void Fill(byte[] lengths, ref int x, int run, byte value)
    {
        if (x + run > lengths.Length)
        {
            throw new InvalidDataException("LZX code lengths overrun their table.");
        }

        lengths.AsSpan(x, run).Fill(value);
        x += run;
    }

    private int CopyUncompressed(int thisRun)
    {
        _bits.ReadRaw(_window.AsSpan(_windowPos, thisRun));
        _windowPos += thisRun;
        return 0;
    }

    private int DecodeMatches(int thisRun, bool aligned)
    {
        var mainTable = _mainTable ?? throw new InvalidDataException("An LZX block has no main tree.");
        while (thisRun > 0)
        {
            var mainElement = mainTable.Decode(ref _bits);
            if (mainElement < NumChars)
            {
                _window[_windowPos++] = (byte)mainElement;
                thisRun--;
                continue;
            }

            mainElement -= NumChars;
            var matchLength = mainElement & NumPrimaryLengths;
            if (matchLength == NumPrimaryLengths)
            {
                matchLength += (_lengthTable ?? throw new InvalidDataException("An LZX block has no length tree.")).Decode(ref _bits);
            }

            matchLength += MinMatch;
            var slot = mainElement >> 3;
            uint matchOffset;
            if (slot > 2)
            {
                matchOffset = aligned ? ReadAlignedOffset(slot) : ReadVerbatimOffset(slot);
                _r2 = _r1;
                _r1 = _r0;
                _r0 = matchOffset;
            }
            else if (slot == 0)
            {
                matchOffset = _r0;
            }
            else if (slot == 1)
            {
                matchOffset = _r1;
                _r1 = _r0;
                _r0 = matchOffset;
            }
            else
            {
                matchOffset = _r2;
                _r2 = _r0;
                _r0 = matchOffset;
            }

            CopyMatch(matchOffset, matchLength);
            thisRun -= matchLength;
        }

        return thisRun;
    }

    private uint ReadVerbatimOffset(int slot)
    {
        if (slot == 3)
        {
            return 1;
        }

        return (uint)(PositionBase[slot] - 2 + _bits.Read(ExtraBits[slot]));
    }

    private uint ReadAlignedOffset(int slot)
    {
        var extra = (int)ExtraBits[slot];
        var offset = (uint)(PositionBase[slot] - 2);
        var alignedTable = _alignedTable ?? throw new InvalidDataException("An aligned LZX block has no aligned tree.");
        if (extra > 3)
        {
            offset += (uint)_bits.Read(extra - 3) << 3;
            offset += (uint)alignedTable.Decode(ref _bits);
        }
        else if (extra == 3)
        {
            offset += (uint)alignedTable.Decode(ref _bits);
        }
        else if (extra > 0)
        {
            offset += (uint)_bits.Read(extra);
        }
        else
        {
            offset = 1;
        }

        return offset;
    }

    private void CopyMatch(uint matchOffset, int matchLength)
    {
        if (_windowPos + matchLength > _windowSize)
        {
            throw new InvalidDataException("An LZX match runs past the end of the window.");
        }

        var produced = _offset + (_windowPos - _framePos);
        if (matchOffset == 0 || matchOffset > produced || matchOffset > _windowSize)
        {
            throw new InvalidDataException("An LZX match refers to data before the start of the stream.");
        }

        var destination = _windowPos;
        var length = matchLength;
        var offset = (int)matchOffset;
        if (offset > _windowPos)
        {
            // The source wraps around the end of the window.
            var fromEnd = offset - _windowPos;
            var source = _windowSize - fromEnd;
            if (fromEnd < length)
            {
                length -= fromEnd;
                while (fromEnd-- > 0)
                {
                    _window[destination++] = _window[source++];
                }

                source = 0;
            }

            while (length-- > 0)
            {
                _window[destination++] = _window[source++];
            }
        }
        else
        {
            var source = destination - offset;
            while (length-- > 0)
            {
                _window[destination++] = _window[source++];
            }
        }

        _windowPos = destination;
    }

    /// <summary>Reverses the encoder's E8 call translation on one output frame.</summary>
    private void UndoIntelTranslation(Span<byte> frame)
    {
        var current = (int)_offset;
        var end = frame.Length - 10;
        var i = 0;
        while (i < end)
        {
            if (frame[i++] != 0xE8)
            {
                current++;
                continue;
            }

            var absolute = frame[i] | (frame[i + 1] << 8) | (frame[i + 2] << 16) | (frame[i + 3] << 24);
            if (absolute >= -current && absolute < _intelFileSize)
            {
                var relative = absolute >= 0 ? absolute - current : absolute + _intelFileSize;
                frame[i] = (byte)relative;
                frame[i + 1] = (byte)(relative >> 8);
                frame[i + 2] = (byte)(relative >> 16);
                frame[i + 3] = (byte)(relative >> 24);
            }

            i += 4;
            current += 5;
        }
    }

    /// <summary>LZX bits: 16-bit little-endian words, most significant bit first.</summary>
    private struct LzxBits : IBitSource
    {
        // Reading a little past the end is how the format pads its last word; much more means truncation.
        private const int ReadPastEndAllowance = 32;

        private readonly byte[] _input;
        private int _position;
        private ulong _buffer;
        private int _bitsLeft;

        public LzxBits(byte[] input)
        {
            _input = input;
            _position = 0;
            _buffer = 0;
            _bitsLeft = 0;
        }

        public int NextBit()
        {
            Ensure(1);
            var bit = (int)(_buffer >> 63);
            Remove(1);
            return bit;
        }

        public int Read(int bitCount)
        {
            if (bitCount == 0)
            {
                return 0;
            }

            Ensure(bitCount);
            var value = (int)(_buffer >> (64 - bitCount));
            Remove(bitCount);
            return value;
        }

        /// <summary>After a frame, drops the rest of the current 16-bit word.</summary>
        public void AlignToWord()
        {
            if (_bitsLeft > 0)
            {
                Ensure(16);
            }

            if ((_bitsLeft & 15) != 0)
            {
                Remove(_bitsLeft & 15);
            }
        }

        /// <summary>Before an uncompressed block: drops 1 to 16 padding bits to reach a byte boundary.</summary>
        public void AlignForUncompressed()
        {
            if (_bitsLeft == 0)
            {
                Ensure(16);
            }

            _bitsLeft = 0;
            _buffer = 0;
        }

        public uint ReadRawUInt32()
        {
            Span<byte> bytes = stackalloc byte[4];
            ReadRaw(bytes);
            return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        }

        public void ReadRaw(Span<byte> destination)
        {
            if (_position + destination.Length > _input.Length)
            {
                throw new InvalidDataException("The LZX data is truncated.");
            }

            _input.AsSpan(_position, destination.Length).CopyTo(destination);
            _position += destination.Length;
        }

        public void SkipByte() => _position++;

        private void Ensure(int bitCount)
        {
            while (_bitsLeft < bitCount)
            {
                var low = NextByte();
                var high = NextByte();
                _buffer |= (ulong)((high << 8) | low) << (64 - 16 - _bitsLeft);
                _bitsLeft += 16;
            }
        }

        private void Remove(int bitCount)
        {
            _buffer <<= bitCount;
            _bitsLeft -= bitCount;
        }

        private int NextByte()
        {
            if (_position < _input.Length)
            {
                return _input[_position++];
            }

            if (_position++ > _input.Length + ReadPastEndAllowance)
            {
                throw new InvalidDataException("The LZX data is truncated.");
            }

            return 0;
        }
    }
}
