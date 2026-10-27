using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;

namespace CloudImaging.ImagingCoreApi.Tests.TestDoubles;

/// <summary>
/// Minimal in-memory <see cref="BlobContainerClient"/>: conditional upload (409 when
/// <c>If-None-Match: *</c> meets an existing blob), download, exists, delete and list by prefix,
/// plus switches that make the next upload or delete fail.
/// </summary>
internal sealed class InMemoryBlobContainerClient : BlobContainerClient
{
    private readonly Dictionary<string, BinaryData> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDictionary<string, string>> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, byte[]>> _stagedBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ETag> _etags = new(StringComparer.Ordinal);
    private int _writes;
    private readonly string _name;

    private ETag Written(string blobName)
    {
        var etag = new ETag($"\"{++_writes}\"");
        _etags[blobName] = etag;
        return etag;
    }

    public InMemoryBlobContainerClient(string name = "setup-profiles")
    {
        _name = name;
    }

    /// <summary>Stored blobs by name.</summary>
    public IReadOnlyDictionary<string, BinaryData> Blobs => _blobs;

    /// <summary>Metadata of stored blobs by name.</summary>
    public IDictionary<string, IDictionary<string, string>> Metadata => _metadata;

    /// <summary>When true, the next upload throws a 500 and the switch resets.</summary>
    public bool FailNextUpload { get; set; }

    /// <summary>When true, the next delete throws a 500 and the switch resets.</summary>
    public bool FailNextDelete { get; set; }

    /// <summary>Blocks staged but not committed, by blob name.</summary>
    public IReadOnlyDictionary<string, Dictionary<string, byte[]>> StagedBlocks => _stagedBlocks;

    /// <summary>True once the container was created.</summary>
    public bool Created { get; private set; }

    public override string Name => _name;

    public override BlobClient GetBlobClient(string blobName) => new InMemoryBlobClient(this, blobName);

    protected override BlockBlobClient GetBlockBlobClientCore(string blobName) => new InMemoryBlockBlobClient(this, blobName);

    public override Task<Response<BlobContainerInfo>> CreateIfNotExistsAsync(PublicAccessType publicAccessType = PublicAccessType.None, IDictionary<string, string>? metadata = null, BlobContainerEncryptionScopeOptions? encryptionScopeOptions = null, CancellationToken cancellationToken = default)
    {
        Created = true;
        return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContainerInfo(new ETag("\"c\""), DateTimeOffset.UtcNow), new FakeResponse(201)));
    }

    public override AsyncPageable<BlobItem> GetBlobsAsync(BlobTraits traits = BlobTraits.None, BlobStates states = BlobStates.None, string? prefix = null, CancellationToken cancellationToken = default)
    {
        var items = _blobs.Keys
            .Where(name => prefix is null || name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => BlobsModelFactory.BlobItem(
                name: name,
                properties: BlobsModelFactory.BlobItemProperties(accessTierInferred: false, contentLength: _blobs[name].ToMemory().Length),
                metadata: _metadata.TryGetValue(name, out var metadata) ? metadata : new Dictionary<string, string>()))
            .ToList();
        return AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues(items, null, new FakeResponse(200))]);
    }

    private sealed class InMemoryBlockBlobClient(InMemoryBlobContainerClient container, string blobName) : BlockBlobClient
    {
        public override string Name => blobName;

        public override string BlobContainerName => container.Name;

        public override async Task<Response<BlockInfo>> StageBlockAsync(string base64BlockId, Stream content, BlockBlobStageBlockOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (!container._stagedBlocks.TryGetValue(blobName, out var blocks))
            {
                blocks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                container._stagedBlocks[blobName] = blocks;
            }

            blocks[base64BlockId] = buffer.ToArray();
            return Response.FromValue(BlobsModelFactory.BlockInfo(null, null, null, null), new FakeResponse(201));
        }

        public override Task<Response<BlobContentInfo>> CommitBlockListAsync(IEnumerable<string> base64BlockIds, CommitBlockListOptions options, CancellationToken cancellationToken = default)
        {
            var blocks = container._stagedBlocks.GetValueOrDefault(blobName) ?? [];
            using var content = new MemoryStream();
            foreach (var id in base64BlockIds)
            {
                if (!blocks.TryGetValue(id, out var block))
                {
                    throw new RequestFailedException(400, "The specified block list is invalid.", "InvalidBlockList", null);
                }

                content.Write(block);
            }

            container._blobs[blobName] = BinaryData.FromBytes(content.ToArray());
            container._metadata[blobName] = new Dictionary<string, string>(options?.Metadata ?? new Dictionary<string, string>());
            container._stagedBlocks.Remove(blobName);
            var info = BlobsModelFactory.BlobContentInfo(container.Written(blobName), DateTimeOffset.UtcNow, null, null, null, null, 0);
            return Task.FromResult(Response.FromValue(info, new FakeResponse(201)));
        }
    }

    private sealed class InMemoryBlobClient(InMemoryBlobContainerClient container, string blobName) : BlobClient
    {
        public override string Name => blobName;

        public override string BlobContainerName => container.Name;

        public override Task<Response<BlobContentInfo>> UploadAsync(BinaryData content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        {
            if (container.FailNextUpload)
            {
                container.FailNextUpload = false;
                throw new RequestFailedException(500, "Upload failed");
            }
            var ifNoneMatch = options?.Conditions?.IfNoneMatch;
            if (ifNoneMatch == ETag.All && container._blobs.ContainsKey(blobName))
            {
                throw new RequestFailedException(409, "The specified blob already exists.", "BlobAlreadyExists", null);
            }
            if (options?.Conditions?.IfMatch is { } ifMatch && (!container._etags.TryGetValue(blobName, out var current) || current != ifMatch))
            {
                throw new RequestFailedException(412, "The condition specified using HTTP conditional header(s) is not met.", "ConditionNotMet", null);
            }
            container._blobs[blobName] = content;
            container._metadata[blobName] = new Dictionary<string, string>(options?.Metadata ?? new Dictionary<string, string>());
            var info = BlobsModelFactory.BlobContentInfo(container.Written(blobName), DateTimeOffset.UtcNow, null, null, null, null, 0);
            return Task.FromResult(Response.FromValue(info, new FakeResponse(201)));
        }

        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            if (!container._blobs.TryGetValue(blobName, out var content))
            {
                throw new RequestFailedException(404, "The specified blob does not exist.", "BlobNotFound", null);
            }
            var properties = BlobsModelFactory.BlobProperties(contentLength: content.ToMemory().Length, metadata: container._metadata.GetValueOrDefault(blobName) ?? new Dictionary<string, string>(), eTag: container._etags.GetValueOrDefault(blobName, new ETag("\"0\"")));
            return Task.FromResult(Response.FromValue(properties, new FakeResponse(200)));
        }

        public override Task<Response<BlobInfo>> SetMetadataAsync(IDictionary<string, string> metadata, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            if (!container._blobs.ContainsKey(blobName))
            {
                throw new RequestFailedException(404, "The specified blob does not exist.", "BlobNotFound", null);
            }
            container._metadata[blobName] = new Dictionary<string, string>(metadata);
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobInfo(new ETag("\"2\""), DateTimeOffset.UtcNow), new FakeResponse(200)));
        }

        public override Task<Response<BlobDownloadResult>> DownloadContentAsync(CancellationToken cancellationToken = default)
        {
            if (!container._blobs.TryGetValue(blobName, out var content))
            {
                throw new RequestFailedException(404, "The specified blob does not exist.", "BlobNotFound", null);
            }
            var result = BlobsModelFactory.BlobDownloadResult(content: content);
            return Task.FromResult(Response.FromValue(result, new FakeResponse(200)));
        }

        public override Task<Response<bool>> ExistsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(container._blobs.ContainsKey(blobName), new FakeResponse(200)));

        public override Task<Stream> OpenReadAsync(BlobOpenReadOptions options, CancellationToken cancellationToken = default)
        {
            if (!container._blobs.TryGetValue(blobName, out var content))
            {
                throw new RequestFailedException(404, "The specified blob does not exist.", "BlobNotFound", null);
            }

            return Task.FromResult<Stream>(new MemoryStream(content.ToArray(), writable: false));
        }

        public override Task<Response<bool>> DeleteIfExistsAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.None, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default)
        {
            if (container.FailNextDelete)
            {
                container.FailNextDelete = false;
                throw new RequestFailedException(500, "Delete failed");
            }
            var removed = container._blobs.Remove(blobName);
            container._metadata.Remove(blobName);
            return Task.FromResult(Response.FromValue(removed, new FakeResponse(removed ? 202 : 404)));
        }
    }
}
