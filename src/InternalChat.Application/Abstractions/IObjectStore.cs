namespace InternalChat.Application.Abstractions;

/// <summary>Free and used bytes in attachment storage.</summary>
/// <param name="UsedBytes">Bytes currently consumed by attachments.</param>
/// <param name="CapacityBytes">Total provisioned capacity.</param>
public readonly record struct StorageCapacity(long UsedBytes, long CapacityBytes)
{
    /// <summary>Fraction of capacity consumed, 0.0 to 1.0.</summary>
    public double UsedFraction => CapacityBytes <= 0 ? 1.0 : (double)UsedBytes / CapacityBytes;
}

/// <summary>
/// Attachment object storage (MinIO or any S3-compatible server).
/// </summary>
/// <remarks>
/// <para>
/// Note what is absent: there is no method returning a retrieval URL. That is deliberate.
/// FR-025 requires membership to be checked on every retrieval "regardless of how the retrieval
/// address was obtained", and a presigned download URL is a bearer capability — whoever holds it
/// gets the bytes, member or not. Handing one out would fail the requirement literally.
/// </para>
/// <para>
/// Retrieval instead goes through the API, which authorizes each request and then hands the byte
/// transfer to the reverse proxy by internal redirect (research.md D7). Uploads still use a
/// one-time ticket, because an upload address grants no read access and the object stays in
/// quarantine until ClamAV clears it.
/// </para>
/// </remarks>
public interface IObjectStore
{
    /// <summary>
    /// Reserves a one-time upload location in the quarantine bucket.
    /// </summary>
    /// <remarks>
    /// Nothing written here is retrievable by anyone. Promotion to the clean bucket happens only
    /// after a clean scan verdict (FR-024).
    /// </remarks>
    Task<Uri> CreateUploadTicketAsync(
        string objectKey,
        string contentType,
        long byteSize,
        TimeSpan validFor,
        CancellationToken cancellationToken = default);

    /// <summary>Moves a scanned-clean object from quarantine into the retrievable bucket.</summary>
    Task PromoteToCleanAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently removes an object — used for infected uploads, deleted messages, and the
    /// retention sweep.
    /// </summary>
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Opens a read stream for the API to hand to the reverse proxy.</summary>
    Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Current capacity, so administrators are alerted before uploads start failing (FR-028).
    /// </summary>
    Task<StorageCapacity> GetCapacityAsync(CancellationToken cancellationToken = default);
}
