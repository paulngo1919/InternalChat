using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternalChat.Infrastructure.Storage;

/// <summary>Configuration for the ClamAV daemon.</summary>
public sealed class ClamAvOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ClamAv";

    /// <summary>Host running <c>clamd</c>.</summary>
    public string Host { get; set; } = "clamav";

    /// <summary>TCP port <c>clamd</c> listens on.</summary>
    public int Port { get; set; } = 3310;

    /// <summary>
    /// How long to wait for a verdict before giving up.
    /// </summary>
    /// <remarks>
    /// Every outbound call needs an explicit timeout (Constitution Performance Requirements).
    /// Generous because a 500 MB video genuinely takes a while to stream and scan, but bounded:
    /// without it a wedged daemon would hold a consumer slot until the process restarted.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// <see cref="IMalwareScanner"/> over ClamAV's <c>INSTREAM</c> protocol (FR-024).
/// </summary>
/// <remarks>
/// <para>
/// <b>INSTREAM rather than SCAN.</b> <c>SCAN</c> takes a path and requires <c>clamd</c> to read the
/// file itself, which would mean sharing a volume between the Worker and the scanner container —
/// and handing a malware scanner a writable mount of the quarantine bucket is a larger blast radius
/// than streaming bytes to it over a socket.
/// </para>
/// <para>
/// <b>Hand-rolled rather than a client library.</b> The protocol is four lines long: send
/// <c>zINSTREAM\0</c>, then length-prefixed chunks, then a zero-length chunk, then read one line
/// back. Principle VIII would permit a library, but the ones available add a dependency and a
/// licence question to save about thirty lines of socket code whose failure modes we would still
/// have to understand.
/// </para>
/// <para>
/// <b>Never throws for an unreachable scanner.</b> It returns <see cref="MalwareScanOutcome.Failed"/>,
/// per the interface contract — a failed scan is a state the row records and a job retries, not a
/// message the broker should redeliver.
/// </para>
/// </remarks>
public sealed partial class ClamAvScanner : IMalwareScanner
{
    /// <summary>
    /// Chunk size for the INSTREAM upload.
    /// </summary>
    /// <remarks>
    /// Comfortably under <c>clamd</c>'s default <c>StreamMaxLength</c> per-chunk tolerance and
    /// large enough that a 500 MB video is not ten thousand syscalls.
    /// </remarks>
    private const int ChunkSize = 64 * 1024;

    private readonly ClamAvOptions _options;
    private readonly ILogger<ClamAvScanner> _logger;

    /// <summary>Creates the scanner.</summary>
    public ClamAvScanner(IOptions<ClamAvOptions> options, ILogger<ClamAvScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MalwareScanResult> ScanAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            return await ScanCoreAsync(content, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down, not the scanner failing. Let it propagate so the message
            // is redelivered rather than recorded as a failed scan.
            throw;
        }
        catch (OperationCanceledException)
        {
            ScanTimedOut(_logger, _options.Timeout);
            return MalwareScanResult.Failed;
        }
        catch (SocketException exception)
        {
            ScannerUnreachable(_logger, _options.Host, _options.Port, exception);
            return MalwareScanResult.Failed;
        }
        catch (IOException exception)
        {
            ScannerUnreachable(_logger, _options.Host, _options.Port, exception);
            return MalwareScanResult.Failed;
        }
    }

    private async Task<MalwareScanResult> ScanCoreAsync(Stream content, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        await client.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);

        await using NetworkStream socket = client.GetStream();

        // 'z' prefix: the command is NUL-terminated and so is the reply. The alternative ('n',
        // newline-terminated) is ambiguous against signature names that contain newlines.
        await socket.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), cancellationToken).ConfigureAwait(false);

        byte[] buffer = new byte[ChunkSize];
        byte[] lengthPrefix = new byte[4];

        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            // Network byte order, which is what clamd reads. Getting this endianness wrong produces
            // a scanner that appears to work and silently truncates every file to a few bytes.
            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, read);

            await socket.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
            await socket.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        // Zero-length chunk terminates the stream and asks for the verdict.
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, 0);
        await socket.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await socket.FlushAsync(cancellationToken).ConfigureAwait(false);

        string reply = await ReadReplyAsync(socket, cancellationToken).ConfigureAwait(false);

        return Interpret(reply);
    }

    private static async Task<string> ReadReplyAsync(Stream socket, CancellationToken cancellationToken)
    {
        StringBuilder reply = new();
        byte[] one = new byte[1];

        while (await socket.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 1)
        {
            if (one[0] == 0)
            {
                break;
            }

            reply.Append((char)one[0]);

            // A well-behaved clamd reply is well under this. The cap exists so a daemon that never
            // sends its terminator cannot grow this buffer without bound.
            if (reply.Length > 4096)
            {
                break;
            }
        }

        return reply.ToString();
    }

    /// <summary>
    /// Turns a <c>clamd</c> reply into a verdict.
    /// </summary>
    /// <remarks>
    /// <b>Anything unrecognised is <see cref="MalwareScanOutcome.Failed"/>, never clean.</b> The
    /// default matters more than the parsing: a reply this code does not understand means the scan
    /// did not demonstrably pass, and the one outcome that must never be reached by falling through
    /// is the one that makes bytes retrievable.
    /// </remarks>
    private MalwareScanResult Interpret(string reply)
    {
        // "stream: OK"
        if (reply.EndsWith("OK", StringComparison.Ordinal)
            && !reply.Contains("FOUND", StringComparison.Ordinal))
        {
            return MalwareScanResult.Clean;
        }

        // "stream: Eicar-Test-Signature FOUND"
        if (reply.EndsWith("FOUND", StringComparison.Ordinal))
        {
            int start = reply.IndexOf(':', StringComparison.Ordinal) + 1;
            int length = reply.Length - start - "FOUND".Length;

            string signature = start > 0 && length > 0
                ? reply.Substring(start, length).Trim()
                : "unknown";

            SignatureMatched(_logger, signature);

            return new MalwareScanResult(MalwareScanOutcome.Infected, signature);
        }

        // "stream: ... ERROR", or anything else. Fails closed.
        UnrecognisedReply(_logger, reply);
        return MalwareScanResult.Failed;
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "ClamAV at {Host}:{Port} is unreachable; recording a failed scan")]
    private static partial void ScannerUnreachable(ILogger logger, string host, int port, Exception exception);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "ClamAV did not answer within {Timeout}; recording a failed scan")]
    private static partial void ScanTimedOut(ILogger logger, TimeSpan timeout);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "ClamAV matched signature {Signature}")]
    private static partial void SignatureMatched(ILogger logger, string signature);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Error, Message = "ClamAV returned an unrecognised reply; failing closed. Reply: {Reply}")]
    private static partial void UnrecognisedReply(ILogger logger, string reply);
}
