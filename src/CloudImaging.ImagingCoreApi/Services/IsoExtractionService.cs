using System.Buffers;
using System.IO;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using Microsoft.Extensions.Logging;

namespace CloudImaging.ImagingCoreApi.Services;

/// <summary>
/// Extracts the actual Windows install image (<c>sources\install.wim</c> or
/// <c>sources\install.esd</c>) from an uploaded Windows installation ISO, at OS image publish
/// time (closes the ISO-support gap: the Client's DISM apply step can only ever apply a genuine
/// WIM/ESD, never a raw ISO container, so an uploaded ISO must be converted once here rather than
/// asking every device to mount/extract it itself).
/// </summary>
public sealed partial class IsoExtractionService
{
    // Preferred first, since retail/volume media ships install.wim; install.esd (compressed,
    // consumer/OEM media) is the fallback.
    private static readonly string[] CandidatePaths = { @"\sources\install.wim", @"\sources\install.esd" };

    /// <summary>
    /// Copy buffer between the ISO entry and the destination. Sized well above the 81 920 byte
    /// default so a multi-GB extract is not paced by round trips to the underlying blob.
    /// </summary>
    private const int CopyBufferBytes = 8 * 1024 * 1024;

    private readonly ILogger<IsoExtractionService> _logger;

    public IsoExtractionService(ILogger<IsoExtractionService> logger) => _logger = logger;

    public sealed record ExtractionResult(bool Found, string? SourcePath, string? Extension, string? FailureReason)
    {
        public static ExtractionResult Success(string sourcePath) =>
            new(true, sourcePath, Path.GetExtension(sourcePath), null);

        public static ExtractionResult Failure(string reason) => new(false, null, null, reason);
    }

    /// <summary>
    /// Locates <c>sources\install.wim</c> or <c>sources\install.esd</c> within the ISO readable
    /// from <paramref name="isoStream"/> (which must support <see cref="Stream.Seek"/>) and
    /// copies it to <paramref name="destination"/>.
    /// </summary>
    public Task<ExtractionResult> ExtractInstallImageAsync(
        Stream isoStream, Stream destination, CancellationToken ct) =>
        ExtractInstallImageAsync(isoStream, (_, _) => Task.FromResult(destination), null, ct);

    /// <summary>
    /// Locates <c>sources\install.wim</c> or <c>sources\install.esd</c> within the ISO readable
    /// from <paramref name="isoStream"/> (which must support <see cref="Stream.Seek"/>) and copies
    /// it to a destination opened on demand by <paramref name="openDestinationAsync"/>.
    ///
    /// <para>
    /// The factory receives the extension of the entry that was found (<c>.wim</c> or <c>.esd</c>),
    /// which is what lets the caller stream straight into a correctly named destination instead of
    /// staging the extracted image on local disk first. That matters at the top of the supported
    /// size range: a 20 GB ISO would otherwise need its multi-GB payload spooled to instance-local
    /// storage, which a Functions instance cannot be relied upon to have. The factory is only
    /// invoked once an entry is actually found, and the stream it returns is owned (and must be
    /// disposed) by the caller, since only the caller knows what to do with a partial write.
    /// </para>
    /// </summary>
    public async Task<ExtractionResult> ExtractInstallImageAsync(
        Stream isoStream,
        Func<string, CancellationToken, Task<Stream>> openDestinationAsync,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        DiscFileSystem fileSystem;
        try
        {
            fileSystem = OpenFileSystem(isoStream);
        }
        catch (Exception ex)
        {
            LogOpenFailed(_logger, ex);
            return ExtractionResult.Failure(
                "Unable to read the uploaded file as an ISO9660/UDF disc image.");
        }

        using (fileSystem)
        {
            foreach (var candidate in CandidatePaths)
            {
                if (!fileSystem.FileExists(candidate))
                {
                    continue;
                }

                LogExtracting(_logger, candidate);
                var destination = await openDestinationAsync(Path.GetExtension(candidate), ct);
                using (var entryStream = fileSystem.OpenFile(candidate, FileMode.Open, FileAccess.Read))
                {
                    await CopyWithProgressAsync(entryStream, destination, progress, ct);
                }

                await destination.FlushAsync(ct);
                return ExtractionResult.Success(candidate);
            }
        }

        return ExtractionResult.Failure(
            "No sources\\install.wim or sources\\install.esd was found inside the uploaded ISO.");
    }

    /// <summary>
    /// Copies the entry in explicit chunks rather than via <c>Stream.CopyToAsync</c>, so the caller
    /// can be told how far through a multi-GB extract it is while it runs.
    /// </summary>
    private static async Task CopyWithProgressAsync(
        Stream source, Stream destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (progress is null)
        {
            await source.CopyToAsync(destination, CopyBufferBytes, ct);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long total = source.CanSeek ? source.Length : 0;
        long copied = 0;

        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(0, CopyBufferBytes), ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), ct);
                copied += count;
                if (total > 0)
                {
                    progress.Report((double)copied / total);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        progress.Report(1.0);
    }

    /// <summary>
    /// Opens the ISO's file system. Modern Windows install media switches to a UDF file system
    /// once install.wim/install.esd exceeds ISO9660's ~4 GB single-file limit, so UDF is tried
    /// first; older/smaller media (e.g. WinPE boot ISOs) that only carry a plain ISO9660 +
    /// Joliet layout fall back to <see cref="CDReader"/>.
    /// </summary>
    private static DiscFileSystem OpenFileSystem(Stream isoStream)
    {
        try
        {
            return new UdfReader(isoStream);
        }
        catch
        {
            isoStream.Position = 0;
            return new CDReader(isoStream, joliet: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Extracting {Path} from uploaded ISO.")]
    private static partial void LogExtracting(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to open the uploaded file as an ISO9660/UDF disc image.")]
    private static partial void LogOpenFailed(ILogger logger, Exception ex);
}
