using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace InternalChat.Infrastructure.Storage;

/// <summary>
/// MinIO implementation of <see cref="IObjectStore"/>, with the quarantine and clean buckets
/// data-model.md describes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two buckets, and the split is the security boundary.</b> Everything lands in
/// <c>attachments-quarantine</c>, from which no path is ever served. Only a clean scan verdict
/// moves an object into <c>attachments</c> (FR-024). A single bucket with a status column would
/// make "is this scanned?" a question the serving path has to remember to ask; two buckets make
/// an unscanned object unreachable by construction.
/// </para>
/// <para>
/// <b>The upload ticket is presigned; there is no presigned download.</b> A presigned upload URL
/// grants the right to write bytes nobody can read, which is harmless. A presigned download URL
/// would be a bearer capability over conversation content, which FR-025 forbids in as many words:
/// retrieval is restricted to members "regardless of how the retrieval address was obtained".
/// Reads therefore go through <see cref="OpenReadAsync"/>, behind an authorized endpoint.
/// </para>
/// <para>
/// <b>Bucket creation is not this class's job.</b> The <c>minio-init</c> Compose service creates
/// both buckets and exits. Creating them lazily here would mean every process that ever writes an
/// object holds bucket-creation rights, and would hide a misconfigured bucket name behind a
/// silently-created empty bucket.
/// </para>
/// </remarks>
public sealed class MinioObjectStore : IObjectStore
{
    private readonly IMinioClient _client;
    private readonly MinioOptions _options;

    /// <summary>Creates the store.</summary>
    public MinioObjectStore(IMinioClient client, IOptions<MinioOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.Bucket)
            || string.IsNullOrWhiteSpace(_options.QuarantineBucket))
        {
            throw new InvalidOperationException(
                "Minio:Bucket and Minio:QuarantineBucket must both be configured.");
        }

        // A single bucket for both would serve unscanned bytes the moment a promotion was skipped.
        if (string.Equals(_options.Bucket, _options.QuarantineBucket, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Minio:Bucket and Minio:QuarantineBucket must differ — the split is what keeps "
                + "unscanned objects unreachable (FR-024).");
        }
    }

    /// <inheritdoc />
    public async Task<Uri> CreateUploadTicketAsync(
        string objectKey,
        string contentType,
        long byteSize,
        TimeSpan validFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(byteSize, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validFor, TimeSpan.Zero);

        // S3 caps presigned URL lifetime at seven days; anything longer is rejected by the server
        // with an opaque error, so it is refused here where the reason can be stated.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(validFor, TimeSpan.FromDays(7));

        PresignedPutObjectArgs args = new PresignedPutObjectArgs()
            .WithBucket(_options.QuarantineBucket)
            .WithObject(objectKey)
            .WithExpiry((int)validFor.TotalSeconds)

            // Bound into the signature, so a ticket issued for a 4 MB PNG cannot be redeemed for a
            // 500 MB executable: MinIO rejects the PUT unless the client sends exactly these
            // headers. This is defence in depth rather than the guarantee — the authoritative
            // check is the re-measure at promotion time, because a signature only constrains what
            // the client *declares*, and declarations are what the scan exists to disbelieve.
            .WithHeaders(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Content-Type"] = string.IsNullOrWhiteSpace(contentType)
                    ? "application/octet-stream"
                    : contentType,
                ["Content-Length"] = byteSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        string url = await _client.PresignedPutObjectAsync(args).ConfigureAwait(false);

        return new Uri(url, UriKind.Absolute);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Server-side copy then delete, rather than a download-and-re-upload through this process.
    /// A 500 MB video would otherwise cross the network twice and sit in the Worker's memory,
    /// on a host whose CPU and memory budget belongs to messaging (research.md D8).
    /// </remarks>
    public async Task PromoteToCleanAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        CopySourceObjectArgs source = new CopySourceObjectArgs()
            .WithBucket(_options.QuarantineBucket)
            .WithObject(objectKey);

        CopyObjectArgs copy = new CopyObjectArgs()
            .WithBucket(_options.Bucket)
            .WithObject(objectKey)
            .WithCopyObjectSource(source);

        await _client.CopyObjectAsync(copy, cancellationToken).ConfigureAwait(false);

        // Deleted only after the copy has been acknowledged. The reverse order would lose the
        // object outright if the copy failed, and a duplicate in quarantine is merely untidy —
        // nothing is served from there, and the retention sweep collects it.
        await RemoveAsync(_options.QuarantineBucket, objectKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Removes the object from both buckets, because the caller does not always know which one
    /// holds it: an infected upload is still in quarantine, a deleted message's attachment is in
    /// the clean bucket, and the retention sweep runs over rows that may be in either. A missing
    /// object is treated as success — this is called on paths that retry.
    /// </remarks>
    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        await RemoveAsync(_options.Bucket, objectKey, cancellationToken).ConfigureAwait(false);
        await RemoveAsync(_options.QuarantineBucket, objectKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads from the clean bucket only. An unscanned object is not merely unauthorized to read,
    /// it is not addressable through this method at all.
    /// </remarks>
    public async Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        // Buffered into memory, which is acceptable only because this path is the fallback: in the
        // deployed configuration the bytes never pass through .NET at all — the endpoint authorizes
        // and nginx streams via X-Accel-Redirect (research.md D7). This exists for tests and for a
        // deployment without the reverse proxy.
        return await ReadAsync(_options.Bucket, objectKey, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<StorageCapacity> GetCapacityAsync(CancellationToken cancellationToken = default)
    {
        long used = 0;

        // Summed across both buckets: quarantine occupies the same disk, and an alert that ignored
        // it would under-report exactly when a backlog of unscanned uploads is what is filling it.
        foreach (string bucket in new[] { _options.Bucket, _options.QuarantineBucket })
        {
            ListObjectsArgs args = new ListObjectsArgs()
                .WithBucket(bucket)
                .WithRecursive(true);

            await foreach (var item in _client
                .ListObjectsEnumAsync(args, cancellationToken)
                .ConfigureAwait(false))
            {
                used += (long)item.Size;
            }
        }

        return new StorageCapacity(used, _options.CapacityBytes);
    }

    /// <inheritdoc />
    public async Task<Stream> OpenQuarantineReadAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);

        try
        {
            return await ReadAsync(_options.QuarantineBucket, objectKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ObjectNotFoundException exception)
        {
            // Translated to IOException because that is what the interface promises and what the
            // scan consumer catches. An interrupted upload (FR-026) leaves exactly this state, and
            // it is an ordinary outcome rather than a MinIO-specific error the Worker should know
            // how to name.
            throw new IOException(
                $"Quarantined object '{objectKey}' is not present; the upload did not complete.",
                exception);
        }
    }

    /// <inheritdoc />
    public async Task ReplaceQuarantineObjectAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default) =>
        await PutAsync(_options.QuarantineBucket, objectKey, content, contentType, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task PutCleanObjectAsync(
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default) =>
        await PutAsync(_options.Bucket, objectKey, content, contentType, cancellationToken)
            .ConfigureAwait(false);

    private async Task PutAsync(
        string bucket,
        string objectKey,
        Stream content,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        ArgumentNullException.ThrowIfNull(content);

        if (content.CanSeek)
        {
            // The caller may have read it already — a probe, a scan. Rewound here rather than at
            // each call site, because forgetting it stores a zero-byte object that looks like a
            // successful write.
            content.Position = 0;
        }

        PutObjectArgs args = new PutObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(content.CanSeek ? content.Length : -1)
            .WithContentType(contentType);

        await _client.PutObjectAsync(args, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stream> ReadAsync(string bucket, string objectKey, CancellationToken cancellationToken)
    {
        // The SDK writes into a callback-supplied stream rather than returning one.
        MemoryStream buffer = new();

        GetObjectArgs args = new GetObjectArgs()
            .WithBucket(bucket)
            .WithObject(objectKey)
            .WithCallbackStream(async (stream, token) =>
                await stream.CopyToAsync(buffer, token).ConfigureAwait(false));

        await _client.GetObjectAsync(args, cancellationToken).ConfigureAwait(false);

        buffer.Position = 0;
        return buffer;
    }

    private async Task RemoveAsync(string bucket, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            RemoveObjectArgs args = new RemoveObjectArgs()
                .WithBucket(bucket)
                .WithObject(objectKey);

            await _client.RemoveObjectAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectNotFoundException)
        {
            // Already gone. Deletion is called from retrying paths — the scan consumer, the
            // retention sweep — and "it is not there" is the state they were asking for.
        }
        catch (BucketNotFoundException)
        {
            // Same reasoning, one level up: nothing to delete. A genuinely missing bucket surfaces
            // loudly on the write paths, which is where it matters.
        }
    }
}
