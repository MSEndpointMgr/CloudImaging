namespace CloudImaging.Contracts.Models;

/// <summary>What <see cref="AutopilotHardwareHash.Inspect"/> found in a 4K hardware hash.</summary>
/// <param name="Decoded">False when the hash is not in the expected binary layout, so nothing below is known.</param>
/// <param name="TpmVersion">The TPM version string recorded in the hash, or null when it has none.</param>
/// <param name="HasTpmEkPub">True when the hash carries the TPM endorsement key, which pre-provisioning and self-deploying modes attest against.</param>
public sealed record AutopilotHashInspection(bool Decoded, string? TpmVersion, bool HasTpmEkPub)
{
    /// <summary>True when the hash has the TPM 2.0 data Autopilot pre-provisioning and self-deploying modes need.</summary>
    public bool PreProvisioningReady => Decoded && HasTpmEkPub && TpmVersion is { } version && version.TrimStart().StartsWith('2');
}

/// <summary>
/// Reads the TPM fields of a Windows Autopilot 4K hardware hash without OA3Tool. The hash is Base64
/// of a binary blob: a 4-byte header, then type/length/value records where each record starts with
/// a little-endian u16 type and a u16 length that includes those 4 bytes. Record 13 is the TPM
/// version string and record 25 the TPM endorsement public key.
/// </summary>
public static class AutopilotHardwareHash
{
    private const int HeaderLength = 4;
    private const int RecordHeaderLength = 4;
    private const ushort TpmVersionRecord = 13;
    private const ushort TpmEkPubRecord = 25;
    private const ushort ChecksumRecord = 0x5343;
    private const ushort ChecksumRecordAlternate = 0x5342;

    public static AutopilotHashInspection Inspect(string? hardwareHash)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(hardwareHash?.Trim() ?? string.Empty);
        }
        catch (FormatException)
        {
            return new AutopilotHashInspection(false, null, false);
        }

        if (bytes.Length < HeaderLength + RecordHeaderLength)
        {
            return new AutopilotHashInspection(false, null, false);
        }

        string? tpmVersion = null;
        var hasEkPub = false;
        var records = 0;
        var offset = HeaderLength;
        while (offset + RecordHeaderLength <= bytes.Length)
        {
            var type = BitConverter.ToUInt16(bytes, offset);
            var length = BitConverter.ToUInt16(bytes, offset + 2);
            if (type is 0 or ChecksumRecord or ChecksumRecordAlternate)
            {
                break;
            }

            if (length < RecordHeaderLength || offset + length > bytes.Length)
            {
                // A record that runs past the end means this is not the layout we know; report nothing rather than guess.
                return new AutopilotHashInspection(false, null, false);
            }

            var valueLength = length - RecordHeaderLength;
            if (type == TpmVersionRecord && valueLength > 0)
            {
                var value = System.Text.Encoding.ASCII.GetString(bytes, offset + RecordHeaderLength, valueLength).TrimEnd('\0').Trim();
                tpmVersion = value.Length > 0 ? value : null;
            }
            else if (type == TpmEkPubRecord)
            {
                hasEkPub = bytes.AsSpan(offset + RecordHeaderLength, valueLength).IndexOfAnyExcept((byte)0) >= 0;
            }

            records++;
            offset += length;
        }

        return records == 0
            ? new AutopilotHashInspection(false, null, false)
            : new AutopilotHashInspection(true, tpmVersion, hasEkPub);
    }
}
