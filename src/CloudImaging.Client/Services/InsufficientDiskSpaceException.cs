using System.IO;

namespace CloudImaging.Client.Services;

/// <summary>Thrown before a download starts when the destination volume cannot hold the file.</summary>
public sealed class InsufficientDiskSpaceException : IOException
{
    /// <summary>Creates the exception for <paramref name="path"/>.</summary>
    public InsufficientDiskSpaceException(string path, long requiredBytes, long availableBytes)
        : base($"Not enough free space to download to \"{path}\": {requiredBytes} bytes needed, {availableBytes} available.")
    {
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    /// <summary>Size of the file to be written.</summary>
    public long RequiredBytes { get; }

    /// <summary>Free space on the destination volume when checked.</summary>
    public long AvailableBytes { get; }
}
