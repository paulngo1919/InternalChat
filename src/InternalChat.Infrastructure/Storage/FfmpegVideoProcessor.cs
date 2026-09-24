using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternalChat.Infrastructure.Storage;

/// <summary>Where ffmpeg lives and how long it may run.</summary>
public sealed class FfmpegOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ffmpeg";

    /// <summary>Path to <c>ffmpeg</c>.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Path to <c>ffprobe</c>.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>
    /// How long any single invocation may run.
    /// </summary>
    /// <remarks>
    /// Every outbound call needs an explicit timeout (Constitution Performance Requirements), and a
    /// subprocess is an outbound call. ffmpeg on a malformed file can spin indefinitely, and without
    /// this it would hold a Worker consumer slot until the process restarted.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// <see cref="IVideoProcessor"/> over the ffmpeg and ffprobe binaries (research.md D8).
/// </summary>
/// <remarks>
/// <para>
/// <b>Temporary files, not pipes.</b> ffprobe needs to seek to read the <c>moov</c> atom, and
/// <c>-movflags +faststart</c> writes the output twice by design — neither works on a non-seekable
/// stream. Piping would mean ffmpeg silently reporting a file as having no <c>moov</c> at all,
/// which is exactly the value being measured.
/// </para>
/// <para>
/// <b>Arguments are passed as a list, never as a formatted command line.</b> The only
/// attacker-influenced value that reaches this class is the file's *content*; paths are generated.
/// Using <see cref="ProcessStartInfo.ArgumentList"/> keeps it that way even if someone later passes
/// a file name through.
/// </para>
/// <para>
/// Registered in the Worker only. The API has no ffmpeg binary and no reason to hold one.
/// </para>
/// </remarks>
public sealed partial class FfmpegVideoProcessor : IVideoProcessor
{
    private readonly FfmpegOptions _options;
    private readonly ILogger<FfmpegVideoProcessor> _logger;

    /// <summary>Creates the processor.</summary>
    public FfmpegVideoProcessor(IOptions<FfmpegOptions> options, ILogger<FfmpegVideoProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<VideoProbe> ProbeAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using TemporaryFile input = await TemporaryFile.FromAsync(content, cancellationToken)
            .ConfigureAwait(false);

        // -show_streams for codecs, -show_format for duration. JSON rather than the default
        // key=value output, which has no escaping and breaks on a file name containing '='.
        (int exitCode, string stdout) = await RunAsync(
            _options.FfprobePath,
            [
                "-v", "quiet",
                "-print_format", "json",
                "-show_streams",
                "-show_format",
                input.Path,
            ],
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            // Unreadable file. Returned as an empty probe rather than thrown — the caller turns it
            // into a refusal, which is an ordinary outcome for a corrupt upload.
            ProbeFailed(_logger, exitCode);
            return new VideoProbe(null, null, null, FastStart: false);
        }

        return Parse(stdout);
    }

    /// <inheritdoc />
    public async Task<Stream?> ExtractPosterAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using TemporaryFile input = await TemporaryFile.FromAsync(content, cancellationToken)
            .ConfigureAwait(false);
        using TemporaryFile output = TemporaryFile.Empty(".jpg");

        (int exitCode, _) = await RunAsync(
            _options.FfmpegPath,
            [
                "-v", "quiet",
                "-y",

                // One second in, not frame zero. Many recordings open on a black or blank frame,
                // and a poster that is a black rectangle is worse than no poster at all.
                "-ss", "1",
                "-i", input.Path,
                "-frames:v", "1",

                // Bounded so a 4K recording does not produce a poster larger than the thumbnail it
                // is displayed at. Width only; height follows the aspect ratio.
                "-vf", "scale=640:-2",
                "-q:v", "4",
                output.Path,
            ],
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0 || !File.Exists(output.Path) || new FileInfo(output.Path).Length == 0)
        {
            // A video shorter than the seek point, or one with no decodable frame. The player falls
            // back to its own first frame; a missing poster is cosmetic and never worth withholding
            // a file someone shared.
            PosterExtractionFailed(_logger, exitCode);
            return null;
        }

        return await output.ReadAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream?> RelocateMoovAtomAsync(
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        using TemporaryFile input = await TemporaryFile.FromAsync(content, cancellationToken)
            .ConfigureAwait(false);
        using TemporaryFile output = TemporaryFile.Empty(".mp4");

        (int exitCode, _) = await RunAsync(
            _options.FfmpegPath,
            [
                "-v", "quiet",
                "-y",
                "-i", input.Path,

                // -c copy: streams are remuxed, never re-encoded. This is the difference between an
                // I/O cost and the CPU cost D8 rejected outright.
                "-c", "copy",
                "-movflags", "+faststart",
                output.Path,
            ],
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0 || !File.Exists(output.Path) || new FileInfo(output.Path).Length == 0)
        {
            RemuxFailed(_logger, exitCode);
            return null;
        }

        return await output.ReadAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads codecs, duration, and <c>moov</c> placement out of ffprobe's JSON.</summary>
    private static VideoProbe Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string? videoCodec = null;
        string? audioCodec = null;

        if (root.TryGetProperty("streams", out JsonElement streams))
        {
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                string? type = stream.TryGetProperty("codec_type", out JsonElement t) ? t.GetString() : null;
                string? name = stream.TryGetProperty("codec_name", out JsonElement n) ? n.GetString() : null;

                // First of each kind wins. A file with two video streams is unusual enough that
                // guessing which one a browser would pick is worse than taking the first.
                videoCodec ??= type == "video" ? name : null;
                audioCodec ??= type == "audio" ? name : null;
            }
        }

        int? duration = null;
        bool fastStart = false;

        if (root.TryGetProperty("format", out JsonElement format))
        {
            if (format.TryGetProperty("duration", out JsonElement d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                && seconds > 0)
            {
                // Rounded UP. A 600.4-second video is over the 600-second limit, and rounding down
                // would admit it — the limit is a ceiling, not a target.
                duration = (int)Math.Ceiling(seconds);
            }

            // WebM is seekable by design and has no moov atom; reported as already-fast so callers
            // need no format branch. For MP4, ffprobe does not report atom order directly — the
            // caller remuxes unconditionally, which is a no-op when it is already at the front.
            string? formatName = format.TryGetProperty("format_name", out JsonElement f)
                ? f.GetString()
                : null;

            fastStart = formatName?.Contains("webm", StringComparison.OrdinalIgnoreCase) == true
                || formatName?.Contains("matroska", StringComparison.OrdinalIgnoreCase) == true;
        }

        return new VideoProbe(videoCodec, audioCodec, duration, fastStart);
    }

    private async Task<(int ExitCode, string StandardOutput)> RunAsync(
        string fileName,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };

        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        timeout.CancelAfter(_options.Timeout);

        try
        {
            process.Start();

            // Read before waiting. A process whose output fills the pipe buffer blocks on write, and
            // waiting for exit first would deadlock on exactly the large outputs worth reading.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillQuietly(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout. The process is killed rather than left behind — an abandoned ffmpeg
            // on a malformed file will consume a core indefinitely.
            KillQuietly(process);
            InvocationTimedOut(_logger, fileName, _options.Timeout);

            return (-1, string.Empty);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            // ffmpeg is not installed. Reported rather than thrown, so a Worker without it still
            // scans and promotes video — just without a poster or a relocated index.
            BinaryMissing(_logger, fileName, exception);
            return (-1, string.Empty);
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and the kill.
        }
    }

    [LoggerMessage(EventId = 4301, Level = LogLevel.Warning, Message = "ffprobe exited with {ExitCode}; treating the file as unreadable")]
    private static partial void ProbeFailed(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 4302, Level = LogLevel.Information, Message = "Poster extraction produced nothing (exit {ExitCode}); the player will use its own first frame")]
    private static partial void PosterExtractionFailed(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 4303, Level = LogLevel.Warning, Message = "Faststart remux failed (exit {ExitCode}); promoting the original, which will start more slowly")]
    private static partial void RemuxFailed(ILogger logger, int exitCode);

    [LoggerMessage(EventId = 4304, Level = LogLevel.Warning, Message = "{FileName} did not finish within {Timeout} and was killed")]
    private static partial void InvocationTimedOut(ILogger logger, string fileName, TimeSpan timeout);

    [LoggerMessage(EventId = 4305, Level = LogLevel.Error, Message = "{FileName} could not be executed. Video posters and faststart remuxing are unavailable.")]
    private static partial void BinaryMissing(ILogger logger, string fileName, Exception exception);

    /// <summary>
    /// A scratch file that deletes itself.
    /// </summary>
    /// <remarks>
    /// ffmpeg needs seekable input and output; see the class remarks. Created under the system
    /// temporary directory with a generated name, so no attacker-influenced value ever reaches a
    /// path.
    /// </remarks>
    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path) => Path = path;

        public string Path { get; }

        public static TemporaryFile Empty(string extension) =>
            new(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"internalchat-{Guid.CreateVersion7():N}{extension}"));

        public static async Task<TemporaryFile> FromAsync(Stream content, CancellationToken cancellationToken)
        {
            TemporaryFile file = Empty(".bin");

            await using (FileStream target = File.Create(file.Path))
            {
                if (content.CanSeek)
                {
                    content.Position = 0;
                }

                await content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            return file;
        }

        public async Task<Stream> ReadAllAsync(CancellationToken cancellationToken)
        {
            // Copied into memory before the file is deleted. Posters are tens of kilobytes; a
            // remuxed video is not, which is why the caller streams it straight back to storage and
            // does not hold it.
            MemoryStream buffer = new();

            await using (FileStream source = File.OpenRead(Path))
            {
                await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            buffer.Position = 0;
            return buffer;
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch (IOException)
            {
                // Left for the operating system's temp sweep. Failing a scan over an undeleted
                // scratch file would be a worse outcome than the file surviving an hour.
            }
        }
    }
}
