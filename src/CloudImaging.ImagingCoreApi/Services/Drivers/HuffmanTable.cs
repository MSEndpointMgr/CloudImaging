namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>
/// Canonical Huffman code built from code lengths and decoded one bit at a time. Shared by the
/// MSZIP (deflate) and LZX decoders, which differ only in how they deliver bits.
/// </summary>
internal sealed class HuffmanTable
{
    private readonly short[] _counts;
    private readonly short[] _symbols;

    public HuffmanTable(ReadOnlySpan<byte> lengths, int maxBits)
    {
        MaxBits = maxBits;
        _counts = new short[maxBits + 1];
        _symbols = new short[lengths.Length];
        foreach (var length in lengths)
        {
            if (length > maxBits)
            {
                throw new InvalidDataException($"A Huffman code length of {length} exceeds the maximum of {maxBits}.");
            }

            _counts[length]++;
        }

        IsEmpty = _counts[0] == lengths.Length;
        if (IsEmpty)
        {
            return;
        }

        // An over-subscribed set of lengths cannot form a prefix code; incomplete codes are allowed
        // and only fail if the data actually uses an unassigned code.
        var left = 1;
        for (var length = 1; length <= maxBits; length++)
        {
            left = (left << 1) - _counts[length];
            if (left < 0)
            {
                throw new InvalidDataException("The compressed data contains an over-subscribed Huffman code.");
            }
        }

        Span<int> offsets = stackalloc int[maxBits + 2];
        for (var length = 1; length <= maxBits; length++)
        {
            offsets[length + 1] = offsets[length] + _counts[length];
        }

        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
            {
                _symbols[offsets[lengths[symbol]]++] = (short)symbol;
            }
        }
    }

    public int MaxBits { get; }

    public bool IsEmpty { get; }

    /// <summary>Decodes one symbol, pulling code bits most significant first from <paramref name="reader"/>.</summary>
    public int Decode<TReader>(ref TReader reader)
        where TReader : struct, IBitSource
    {
        if (IsEmpty)
        {
            throw new InvalidDataException("The compressed data uses an empty Huffman code.");
        }

        int code = 0, first = 0, index = 0;
        for (var length = 1; length <= MaxBits; length++)
        {
            code |= reader.NextBit();
            int count = _counts[length];
            if (code - count < first)
            {
                return _symbols[index + (code - first)];
            }

            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }

        throw new InvalidDataException("The compressed data contains an invalid Huffman code.");
    }
}

/// <summary>Delivers single bits to <see cref="HuffmanTable.Decode{TReader}"/>.</summary>
internal interface IBitSource
{
    int NextBit();
}
