using System.IO;
using System.Security.Cryptography;

namespace CloudImaging.Client.Services;

/// <summary>
/// SHA-256 over large image files.
/// </summary>
/// <remarks>
/// Replaces <c>SHA256.HashDataAsync(File.OpenRead(path))</c>, which reads through the default
/// 4 KB <see cref="FileStream"/> buffer. At that size a 10 GB image is roughly 2.5 million
/// round trips to a USB drive, and verification of a cached image took long enough that
/// operators reported the client as hung. A 1 MiB buffer with
/// <see cref="FileOptions.SequentialScan"/> cuts that by three orders of magnitude.
///
/// It also reports progress. Verification happens before DISM starts, so without it the UI has
/// nothing to show for the whole of what is often the longest single wait in the pipeline.
/// </remarks>
internal static class ImageHasher
{
    /// <summary>1 MiB. Large enough to amortise USB latency, small enough not to matter in WinPE.</summary>
    private const int BufferSize = 1024 * 1024;

    /// <summary>Computes the lowercase hex SHA-256 of <paramref name="filePath"/>.</summary>
    /// <param name="filePath">File to hash.</param>
    /// <param name="onProgress">Receives 0 to 100 as the file is read. Called at most once per percent.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string> ComputeAsync(
        string filePath,
        Action<int>? onProgress = null,
        CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        var total = stream.Length;
        long read = 0;
        var lastPercent = -1;

        onProgress?.Invoke(0);

        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;

            hash.AppendData(buffer, 0, count);
            read += count;

            if (onProgress is null || total <= 0) continue;

            var percent = (int)(read * 100 / total);
            if (percent == lastPercent) continue;

            lastPercent = percent;
            onProgress(percent);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
