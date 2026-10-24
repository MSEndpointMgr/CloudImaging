using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>A stored script or answer file text.</summary>
/// <param name="Sha256">Lowercase hex SHA-256 of the text without BOM.</param>
/// <param name="SizeBytes">Length of the text in bytes.</param>
/// <param name="UploadedAt">When the text was first uploaded; null when the metadata is missing.</param>
public sealed record SetupProfileContentInfo(string Sha256, long SizeBytes, DateTimeOffset? UploadedAt);

/// <summary>
/// Script and answer file texts: one immutable blob <c>content/{sha256}</c> per distinct text in
/// container setup-profiles, shared by every version and profile that references it. Stored as
/// UTF-8 without BOM, so the device checks the hash against exactly the bytes it downloads.
/// </summary>
public sealed class SetupProfileContentRepository
{
    /// <summary>Blob name prefix of every text.</summary>
    public const string Prefix = "content/";

    /// <summary>Upload kind of a script.</summary>
    public const string ScriptKind = "script";

    /// <summary>Upload kind of an answer file.</summary>
    public const string AnswerFileKind = "answerFile";

    private const string UploadedAtKey = "uploadedAt";
    private const string KindKey = "kind";
    private readonly BlobContainerClient _container;

    /// <param name="container">The setup-profiles blob container.</param>
    public SetupProfileContentRepository(BlobContainerClient container) => _container = container;

    /// <summary>Blob name of a text.</summary>
    public static string BlobNameFor(string sha256) => Prefix + sha256;

    /// <summary>True for 64 lowercase hex characters.</summary>
    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Lowercase hex SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Stores <paramref name="bytes"/> (already without BOM) once. An existing blob with the same
    /// hash is success, because the hash names exactly these bytes.
    /// </summary>
    public async Task<SetupProfileContentInfo> StoreAsync(ReadOnlyMemory<byte> bytes, string kind, DateTimeOffset now, CancellationToken ct = default)
    {
        var sha256 = Sha256(bytes.Span);
        var metadata = new Dictionary<string, string>
        {
            [UploadedAtKey] = now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            [KindKey] = kind,
        };
        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "text/plain; charset=utf-8" },
            Metadata = metadata,
        };
        var blob = _container.GetBlobClient(BlobNameFor(sha256));
        try
        {
            await blob.UploadAsync(new BinaryData(bytes), options, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 409 or 412)
        {
            // Already stored by an earlier upload of the same text. A fresh upload time keeps the
            // cleanup from removing it before the save that is about to reference it.
            await blob.SetMetadataAsync(metadata, cancellationToken: ct);
        }
        return new SetupProfileContentInfo(sha256, bytes.Length, now);
    }

    /// <summary>Size and upload time of a stored text, or null when it does not exist.</summary>
    public async Task<SetupProfileContentInfo?> GetInfoAsync(string sha256, CancellationToken ct = default)
    {
        try
        {
            var properties = (await _container.GetBlobClient(BlobNameFor(sha256)).GetPropertiesAsync(cancellationToken: ct)).Value;
            return new SetupProfileContentInfo(sha256, properties.ContentLength, UploadedAt(properties.Metadata));
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>The text's bytes, or null when it does not exist.</summary>
    public async Task<BinaryData?> DownloadAsync(string sha256, CancellationToken ct = default)
    {
        try
        {
            return (await _container.GetBlobClient(BlobNameFor(sha256)).DownloadContentAsync(ct)).Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { return null; }
    }

    /// <summary>Lists every stored text.</summary>
    public async IAsyncEnumerable<SetupProfileContentInfo> ListAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var item in _container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, Prefix, ct))
        {
            yield return new SetupProfileContentInfo(item.Name[Prefix.Length..], item.Properties?.ContentLength ?? 0, UploadedAt(item.Metadata));
        }
    }

    /// <summary>Deletes a text.</summary>
    public async Task DeleteAsync(string sha256, CancellationToken ct = default) =>
        await _container.GetBlobClient(BlobNameFor(sha256)).DeleteIfExistsAsync(cancellationToken: ct);

    private static DateTimeOffset? UploadedAt(IDictionary<string, string>? metadata) =>
        metadata is not null && metadata.TryGetValue(UploadedAtKey, out var text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}
