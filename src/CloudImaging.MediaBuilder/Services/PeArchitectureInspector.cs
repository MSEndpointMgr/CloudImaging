using System.IO;
using CloudImaging.Contracts.Models;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Reads the target machine type out of a PE (.exe/.dll) file's COFF header, without loading or
/// executing the file, so a wrong-architecture Cloud Imaging Client build can be rejected before
/// it's mounted into a WinPE image: whether it came from a downloaded GitHub release
/// (<see cref="GitHubReleasesClient"/>) or a manually specified local folder
/// (<see cref="BootImageGenerationService"/>) (todo/arm64-support.md).
/// </summary>
public static class PeArchitectureInspector
{
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;
    private const uint PeSignature = 0x00004550; // "PE\0\0"

    /// <summary>
    /// Reads the machine type from <paramref name="peFilePath"/>'s COFF header. Returns null if
    /// the file isn't a recognizable PE image, or targets a machine type this product doesn't
    /// build for. Callers should treat null as "unknown" rather than silently guessing.
    /// </summary>
    public static MachineArchitecture? ReadArchitecture(string peFilePath)
    {
        using var stream = File.OpenRead(peFilePath);
        using var reader = new BinaryReader(stream);

        if (stream.Length < 0x40)
            return null;

        // DOS header: e_lfanew (offset of the PE header) is a 4-byte value at offset 0x3C.
        stream.Seek(0x3C, SeekOrigin.Begin);
        var peHeaderOffset = reader.ReadInt32();
        if (peHeaderOffset <= 0 || peHeaderOffset + 6 > stream.Length)
            return null;

        stream.Seek(peHeaderOffset, SeekOrigin.Begin);
        if (reader.ReadUInt32() != PeSignature)
            return null;

        // The COFF file header immediately follows the 4-byte PE signature; Machine is its first 2-byte field.
        return reader.ReadUInt16() switch
        {
            ImageFileMachineAmd64 => MachineArchitecture.X64,
            ImageFileMachineArm64 => MachineArchitecture.Arm64,
            _ => null,
        };
    }
}
