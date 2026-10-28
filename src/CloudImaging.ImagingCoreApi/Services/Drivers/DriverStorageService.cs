using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using CloudImaging.ImagingCoreApi.Repositories.Drivers;

namespace CloudImaging.ImagingCoreApi.Services.Drivers;

/// <summary>Outcome of storing one vendor file.</summary>
/// <param name="Succeeded">True when the file was committed.</param>
/// <param name="Reason">Why it failed.</param>
/// <param name="Size">Bytes read.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the bytes read.</param>
public sealed record DriverDownloadOutcome(bool Succeeded, string? Reason, long Size, string? Sha256)
{
    /// <summary>A failure with a reason.</summary>
    public static DriverDownloadOutcome Fail(string reason, long size = 0, string? sha256 = null) => new(false, reason, size, sha256);
}

/// <summary>
/// Downloads vendor files and catalogs into the <c>drivers</c> container (research R2, R5). Every
/// URL, and every redirect, must be https on an allowlisted vendor host with no credentials. Files
/// are streamed into staged blocks while their SHA-256 is computed; the block list is committed
/// only when length and hash match the vendor's values, so an unverified file is never visible.
/// </summary>
public sealed class DriverStorageService
{
    /// <summary>Name of the HTTP client used for vendor hosts (no automatic redirects).</summary>
    public const string HttpClientName = "driver-vendor";

    /// <summary>Lifetime of the read SAS handed to devices and Media Builder.</summary>
    public static readonly TimeSpan ReadLinkLifetime = TimeSpan.FromMinutes(60);

    /// <summary>Hosts Core may download from.</summary>
    public static readonly IReadOnlyList<string> AllowedHosts =
        ["hpia.hpcloud.hp.com", "ftp.hp.com", "ftp.ext.hp.com", "downloads.dell.com", "dl.dell.com"];

    private const int BlockSize = 4 * 1024 * 1024;
    private const int MaxRedirects = 5;

    /// <summary>Largest vendor file stored when the catalog lists no size.</summary>
    public const long MaxVendorFileBytes = 8L * 1024 * 1024 * 1024;

    private readonly BlobServiceClient _blobService;
    private readonly BlobContainerClient _container;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    /// <param name="blobService">Signs read links.</param>
    /// <param name="container">The <c>drivers</c> container.</param>
    /// <param name="http">HTTP client that does not follow redirects by itself.</param>
    /// <param name="time">Clock.</param>
    public DriverStorageService(BlobServiceClient blobService, BlobContainerClient container, HttpClient http, TimeProvider time)
    {
        _blobService = blobService;
        _container = container;
        _http = http;
        _time = time;
    }

    /// <summary>Creates the <c>drivers</c> container when missing.</summary>
    public async Task EnsureContainerAsync(CancellationToken ct = default) =>
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);

    /// <summary>Checks a vendor URL; returns null when allowed, otherwise the reason.</summary>
    public static string? CheckUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "The download address is not a valid URL.";
        }

        return CheckUri(uri);
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="blobName"/>, verifying its length (when
    /// known) and SHA-256 against the catalog's values before committing it.
    /// </summary>
    public async Task<DriverDownloadOutcome> DownloadAsync(string url, string blobName, string expectedSha256, long? expectedSize, CancellationToken ct = default)
    {
        using var response = await GetAsync(url, ct);
        if (response.Failure is not null)
        {
            return DriverDownloadOutcome.Fail(response.Failure);
        }

        var blob = _container.GetBlockBlobClient(blobName);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var blockIds = new List<string>();
        var buffer = new byte[BlockSize];
        long total = 0;
        await using (var body = await response.Message!.Content.ReadAsStreamAsync(ct))
        {
            while (true)
            {
                var filled = await FillAsync(body, buffer, ct);
                if (filled == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, filled);
                total += filled;
                if (total > (expectedSize ?? MaxVendorFileBytes))
                {
                    return DriverDownloadOutcome.Fail($"The file is larger than the {expectedSize ?? MaxVendorFileBytes} bytes allowed for it.", total);
                }

                var blockId = Convert.ToBase64String(Encoding.ASCII.GetBytes(blockIds.Count.ToString("D6", CultureInfo.InvariantCulture)));
                using var block = new MemoryStream(buffer, 0, filled, writable: false);
                await blob.StageBlockAsync(blockId, block, cancellationToken: ct);
                blockIds.Add(blockId);
                if (filled < buffer.Length)
                {
                    break;
                }
            }
        }

        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (expectedSize is { } size && total != size)
        {
            return DriverDownloadOutcome.Fail($"The file is {total} bytes; the catalog lists {size}.", total, sha256);
        }

        if (!string.Equals(sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            return DriverDownloadOutcome.Fail("The file's SHA-256 does not match the vendor's catalog.", total, sha256);
        }

        var options = new CommitBlockListOptions
        {
            Metadata = new Dictionary<string, string> { ["sha256"] = sha256, ["verified"] = "true" },
        };
        await blob.CommitBlockListAsync(blockIds, options, ct);
        return new DriverDownloadOutcome(true, null, total, sha256);
    }

    /// <summary>Downloads a catalog into memory, refusing anything larger than a cabinet may be.</summary>
    public async Task<(byte[]? Content, string? Failure)> DownloadCatalogAsync(string url, CancellationToken ct = default)
    {
        using var response = await GetAsync(url, ct);
        if (response.Failure is not null)
        {
            return (null, response.Failure);
        }

        await using var body = await response.Message!.Content.ReadAsStreamAsync(ct);
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            if (content.Length + read > CabinetReader.MaxCabinetBytes)
            {
                return (null, "The catalog is larger than expected.");
            }

            content.Write(buffer, 0, read);
        }

        return (content.ToArray(), null);
    }

    /// <summary>Returns a read link to one blob, valid for 60 minutes.</summary>
    public async Task<(string Url, DateTimeOffset ExpiresAt)> GetReadLinkAsync(string blobName, CancellationToken ct = default)
    {
        var url = await BlobSasUrlGenerator.GenerateAsync(_blobService, DriverTables.Container, blobName, BlobSasPermissions.Read, ReadLinkLifetime, ct);
        return (url, _time.GetUtcNow() + ReadLinkLifetime);
    }

    /// <summary>Deletes a stored file.</summary>
    public async Task DeleteAsync(string blobName, CancellationToken ct = default) =>
        await _container.GetBlobClient(blobName).DeleteIfExistsAsync(cancellationToken: ct);

    /// <summary>Names of the stored blobs under <paramref name="prefix"/>.</summary>
    public async Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default)
    {
        var names = new List<string>();
        await foreach (var item in _container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            names.Add(item.Name);
        }

        return names;
    }

    /// <summary>A write link the browser stages a custom package into.</summary>
    public async Task<string> GetUploadLinkAsync(string blobName, TimeSpan lifetime, CancellationToken ct = default) =>
        await BlobSasUrlGenerator.GenerateAsync(_blobService, DriverTables.Container, blobName, BlobSasPermissions.Create | BlobSasPermissions.Write, lifetime, ct);

    /// <summary>
    /// Commits a staged package and copies it, hashing as it goes, to <paramref name="finalBlob"/>,
    /// which no upload link can write; the staging blob is deleted. Refuses a package over
    /// <paramref name="maxBytes"/> or of another size than declared before reading it. For a ZIP
    /// also counts the <c>.inf</c> entries in its central directory.
    /// </summary>
    public async Task<(string? Sha256, long Size, int InfCount, string? Failure)> CommitPackageAsync(string stagingBlob, IReadOnlyList<string> blockIds, bool zip, string finalBlob, long maxBytes, long declaredBytes, CancellationToken ct = default)
    {
        const string Incomplete = "The upload is incomplete. Upload the package again.";
        try
        {
            await _container.GetBlockBlobClient(stagingBlob).CommitBlockListAsync(blockIds, new CommitBlockListOptions(), ct);
        }
        catch (Azure.RequestFailedException)
        {
            return (null, 0, 0, Incomplete);
        }

        var staged = _container.GetBlobClient(stagingBlob);
        string sha256;
        long size = 0;
        try
        {
            var length = (await staged.GetPropertiesAsync(cancellationToken: ct)).Value.ContentLength;
            if (length > maxBytes)
            {
                return (null, length, 0, "The package must be 5 GB or smaller.");
            }

            if (length != declaredBytes)
            {
                return (null, length, 0, Incomplete);
            }

            var target = _container.GetBlockBlobClient(finalBlob);
            var finalIds = new List<string>();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[BlockSize];
            await using (var content = await staged.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct))
            {
                int filled;
                while ((filled = await FillAsync(content, buffer, ct)) > 0)
                {
                    size += filled;
                    if (size > declaredBytes)
                    {
                        return (null, size, 0, Incomplete);
                    }

                    hash.AppendData(buffer, 0, filled);
                    var blockId = Convert.ToBase64String(Encoding.ASCII.GetBytes(finalIds.Count.ToString("D6", CultureInfo.InvariantCulture)));
                    using var block = new MemoryStream(buffer, 0, filled, writable: false);
                    await target.StageBlockAsync(blockId, block, cancellationToken: ct);
                    finalIds.Add(blockId);
                }
            }

            if (size != declaredBytes)
            {
                return (null, size, 0, Incomplete);
            }

            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            await target.CommitBlockListAsync(finalIds, new CommitBlockListOptions { Metadata = new Dictionary<string, string> { ["sha256"] = sha256, ["verified"] = "true" } }, ct);
        }
        finally
        {
            await staged.DeleteIfExistsAsync(cancellationToken: ct);
        }

        if (!zip)
        {
            return (sha256, size, 0, null);
        }

        try
        {
            await using var content = await _container.GetBlobClient(finalBlob).OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct);
            using var archive = new System.IO.Compression.ZipArchive(content, System.IO.Compression.ZipArchiveMode.Read);
            return (sha256, size, archive.Entries.Count(e => e.Name.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)), null);
        }
        catch (InvalidDataException)
        {
            await DeleteAsync(finalBlob, ct);
            return (null, size, 0, "The package is not a valid ZIP file.");
        }
    }

    /// <summary>
    /// True when the vendor still hosts <paramref name="url"/>: a HEAD through the host allowlist
    /// answered 200 within <paramref name="timeout"/>. A timeout or refused host counts as unavailable.
    /// </summary>
    public async Task<bool> ProbeAsync(string url, TimeSpan timeout, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await SendAsync(HttpMethod.Head, url, limit.Token);
            return response.Failure is null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string? CheckUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return "The download address is not https.";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return "The download address contains credentials.";
        }

        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return $"The download host {uri.Host} is not an allowed vendor host.";
        }

        return null;
    }

    private Task<VendorResponse> GetAsync(string url, CancellationToken ct) => SendAsync(HttpMethod.Get, url, ct);

    private async Task<VendorResponse> SendAsync(HttpMethod method, string url, CancellationToken ct)
    {
        if (CheckUrl(url) is { } invalid)
        {
            return new VendorResponse(null, invalid);
        }

        var uri = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(method, uri);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                return new VendorResponse(null, $"Can't reach {uri.Host}: {ex.Message}");
            }

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                if (CheckUri(next) is { } refused)
                {
                    return new VendorResponse(null, $"The vendor host redirected to {next.Host}, which is not allowed: {refused}");
                }

                uri = next;
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                return new VendorResponse(null, $"{uri.Host} answered {status}.");
            }

            return new VendorResponse(response, null);
        }

        return new VendorResponse(null, "The vendor host redirected too many times.");
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<int> FillAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        return filled;
    }

    private sealed record VendorResponse(HttpResponseMessage? Message, string? Failure) : IDisposable
    {
        public void Dispose() => Message?.Dispose();
    }
}
