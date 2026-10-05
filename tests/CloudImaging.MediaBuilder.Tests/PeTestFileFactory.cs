using System.IO;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Writes a minimal (non-executable) PE file containing just enough of a DOS header, PE
/// signature, and COFF file header for <c>PeArchitectureInspector.ReadArchitecture</c> to
/// resolve a machine type, without needing a real compiled binary of each architecture on disk.
/// </summary>
internal static class PeTestFileFactory
{
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;

    public static void WriteMinimalPeFile(string path, bool arm64)
    {
        var machine = arm64 ? ImageFileMachineArm64 : ImageFileMachineAmd64;
        var bytes = new byte[70];

        // DOS header's e_lfanew (offset of the PE header) is a 4-byte value at offset 0x3C.
        BitConverter.GetBytes(64).CopyTo(bytes, 0x3C);

        // "PE\0\0" signature at the e_lfanew offset (64).
        bytes[64] = (byte)'P';
        bytes[65] = (byte)'E';
        bytes[66] = 0;
        bytes[67] = 0;

        // COFF file header's Machine field: the first 2 bytes right after the signature.
        BitConverter.GetBytes(machine).CopyTo(bytes, 68);

        File.WriteAllBytes(path, bytes);
    }
}
