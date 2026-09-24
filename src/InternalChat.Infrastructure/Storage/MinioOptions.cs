namespace InternalChat.Infrastructure.Storage;

/// <summary>Configuration for MinIO attachment storage (data-model.md, "MinIO layout").</summary>
public sealed class MinioOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Minio";

    /// <summary>Host and port of the S3 endpoint, without a scheme — for example <c>minio:9000</c>.</summary>
    /// <remarks>
    /// The MinIO SDK takes host and TLS as separate inputs rather than a URL, which is why this is
    /// not a <see cref="Uri"/>: carrying a scheme here would let it disagree with
    /// <see cref="UseTls"/>, and only one of the two would win.
    /// </remarks>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Access key.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Secret key.</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Whether to reach the endpoint over TLS.</summary>
    /// <remarks>
    /// False in Compose, where MinIO is on the internal <c>backend</c> network and never exposed;
    /// true anywhere the hop leaves the host.
    /// </remarks>
    public bool UseTls { get; set; }

    /// <summary>Bucket holding scanned-clean objects. Reachable only via the nginx internal redirect.</summary>
    public string Bucket { get; set; } = "attachments";

    /// <summary>
    /// Bucket holding uploads awaiting a verdict. No path is ever served from here (data-model.md).
    /// </summary>
    public string QuarantineBucket { get; set; } = "attachments-quarantine";

    /// <summary>
    /// Provisioned capacity in bytes, used to raise the administrator alert before uploads start
    /// failing (FR-028).
    /// </summary>
    /// <remarks>
    /// Configured rather than probed. MinIO reports the free space of the underlying filesystem,
    /// which on a shared application host is also PostgreSQL's and the container images' — sizing
    /// the alert off that would fire when something entirely unrelated filled the disk. This is the
    /// budget attachments are allowed, which is the number an administrator actually set.
    /// </remarks>
    public long CapacityBytes { get; set; } = 500L * 1024 * 1024 * 1024;

    /// <summary>How long an upload ticket stays valid.</summary>
    /// <remarks>
    /// Long enough for a 500 MB video on an indifferent connection, short enough that a leaked
    /// ticket is not a durable write capability. It grants no read access in any case: it addresses
    /// the quarantine bucket, from which nothing is ever served.
    /// </remarks>
    public TimeSpan UploadTicketLifetime { get; set; } = TimeSpan.FromMinutes(30);
}
