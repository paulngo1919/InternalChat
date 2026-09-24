using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InternalChat.Infrastructure.Meetings;

/// <summary>Where LiveKit lives and how it is authenticated.</summary>
public sealed class LiveKitOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "LiveKit";

    /// <summary>The WebSocket URL clients connect to, on the media host.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The HTTP base address for LiveKit's server API. Defaults from <see cref="Url"/>.</summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>API key. Identifies this application to LiveKit.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// API secret. Signs both join tokens and server-API calls.
    /// </summary>
    /// <remarks>
    /// The single most sensitive value in the deployment: anything holding it can mint a token for
    /// any room and any identity, which is the whole of FR-041's enforcement. It never leaves the
    /// server and never appears in a response.
    /// </remarks>
    public string ApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// How long the media host may be unreachable before availability is re-probed.
    /// </summary>
    /// <remarks>
    /// The probe result is cached for this long so a media-host outage costs one request per
    /// window rather than one per join attempt — a down host must not become a queue of timing-out
    /// requests on the API, which is how a second-host failure becomes a first-host outage.
    /// </remarks>
    public TimeSpan AvailabilityCacheDuration { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// <see cref="IMeetingTokenIssuer"/> over LiveKit (T186, research.md D12).
/// </summary>
/// <remarks>
/// <para>
/// <b>The token is a hand-built JWT rather than an SDK call, and that is a Principle VIII
/// decision.</b> A LiveKit access token is an HS256 JWT with a <c>video</c> grant claim — about
/// forty lines, using primitives already in the BCL. Taking the SDK would add a dependency and a
/// licence to re-verify at every version bump to save those lines, and the same reasoning already
/// applies to the hand-rolled ClamAV INSTREAM client.
/// </para>
/// <para>
/// <b>This class performs no authorization and must never be given any.</b> The media server trusts
/// its token completely — it runs no membership check of its own — so FR-041 is enforced entirely by
/// the caller refusing to reach this method. Putting a check here would create a second place the
/// rule lives, and the two would eventually disagree.
/// </para>
/// <para>
/// <b>Availability is cached and failures are never thrown to the caller.</b> The media host is the
/// only component on a second machine, and constitution v1.2.0 requires the application to stay
/// fully functional without it. An unreachable host reports unavailable; it does not produce a
/// timeout on a request path that messaging shares.
/// </para>
/// </remarks>
public sealed partial class LiveKitTokenIssuer : IMeetingTokenIssuer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly LiveKitOptions _options;
    private readonly ILogger<LiveKitTokenIssuer> _logger;

    /// <summary>
    /// The last availability probe, shared across requests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Static because this service is registered per-request through <c>AddHttpClient</c>, and a
    /// cache that died with the instance would probe on every join attempt — which is exactly the
    /// per-request timeout against a down media host that the cache exists to prevent.
    /// </para>
    /// <para>
    /// An immutable class replaced wholesale rather than a mutable tuple. A
    /// <c>(DateTimeOffset, bool)</c> is wider than a machine word, so assigning it from two threads
    /// can tear — producing a reading that pairs one probe's timestamp with another's result, which
    /// would pin availability to a stale value for as long as the clock said it was fresh. A
    /// reference assignment is atomic, so a reader sees one probe or another and never a mixture.
    /// </para>
    /// </remarks>
    private static volatile AvailabilityProbe _availability = new(DateTimeOffset.MinValue, false);

    /// <summary>Creates the issuer.</summary>
    public LiveKitTokenIssuer(
        HttpClient http,
        IOptions<LiveKitOptions> options,
        ILogger<LiveKitTokenIssuer> logger)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _http = http;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            // Validated lazily, in the constructor, for the same reason as WebPushSender and the
            // MinIO client: both hosts share AddInfrastructure(), and a host that never starts a
            // meeting must not fail to boot over configuration it does not use.
            throw new InvalidOperationException(
                $"{LiveKitOptions.SectionName}:ApiKey and :ApiSecret are required to issue meeting tokens.");
        }
    }

    /// <inheritdoc />
    public Task<MeetingAccessToken> IssueAsync(
        Guid meetingId,
        Guid employeeId,
        string displayName,
        TimeSpan validFor,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(validFor, TimeSpan.Zero);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now.Add(validFor);

        // The room name IS the meeting id (data-model.md). One identifier rather than a mapping,
        // so a webhook naming a room always resolves to a meeting or to nothing — never to the
        // wrong one.
        string room = meetingId.ToString();

        Dictionary<string, object> payload = new(StringComparer.Ordinal)
        {
            ["iss"] = _options.ApiKey,
            ["sub"] = employeeId.ToString(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),

            // The display name LiveKit shows on the tile. Taken from the directory rather than from
            // the client, so nobody can join a meeting under a colleague's name.
            ["name"] = displayName,

            ["video"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["room"] = room,
                ["roomJoin"] = true,

                // Publishing and subscribing: two-way audio and video for everyone (FR-042), plus
                // screen share (FR-048). Data is enabled for LiveKit's own signalling.
                ["canPublish"] = true,
                ["canSubscribe"] = true,
                ["canPublishData"] = true,

                // Explicitly NOT granted. roomCreate would let a client conjure rooms this system
                // has no meeting row for; roomAdmin would let one participant remove another, which
                // is a moderation feature nothing has specified and which would bypass every audit
                // record in FR-051.
                ["roomCreate"] = false,
                ["roomAdmin"] = false,
                ["roomList"] = false,
            },
        };

        string token = Sign(payload, _options.ApiSecret);

        return Task.FromResult(new MeetingAccessToken(token, new Uri(_options.Url), expiresAt));
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        AvailabilityProbe cached = _availability;

        if (DateTimeOffset.UtcNow - cached.CheckedAt < _options.AvailabilityCacheDuration)
        {
            return cached.Available;
        }

        bool result = await ProbeAsync(cancellationToken).ConfigureAwait(false);

        // Unsynchronised on purpose. Two concurrent probes both writing is harmless — they are
        // measuring the same thing moments apart — and a lock here would serialise every join
        // attempt behind one HTTP call to the host that is, by hypothesis, possibly down.
        _availability = new AvailabilityProbe(DateTimeOffset.UtcNow, result);

        return result;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reads the live count from LiveKit rather than from our own rows. The rows are updated by
    /// webhooks, which are at-least-once and can be delayed; the FR-043 ceiling is a capacity guard
    /// and wants the truth from the thing that actually holds the participants.
    /// </remarks>
    public async Task<int> GetActiveParticipantCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using HttpRequestMessage request = ServerApiRequest("/twirp/livekit.RoomService/ListRooms", "{}");

            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                ServerApiFailed(_logger, (int)response.StatusCode);
                return 0;
            }

            RoomList? rooms = await response.Content
                .ReadFromJsonAsync<RoomList>(SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return rooms?.Rooms?.Sum(room => room.NumParticipants) ?? 0;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Unreachable. Reported as zero rather than thrown: the caller is deciding whether to
            // refuse a meeting for capacity, and refusing every meeting because the count could not
            // be read would turn a media-host blip into a total meetings outage. The availability
            // probe is what actually gates starting one.
            CountUnavailable(_logger, exception);
            return 0;
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = ServerApiRequest("/twirp/livekit.RoomService/ListRooms", "{}");

            using HttpResponseMessage response = await _http
                .SendAsync(request, cancellationToken).ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            MediaHostUnreachable(_logger, exception);
            return false;
        }
    }

    /// <summary>
    /// Builds a signed server-API request.
    /// </summary>
    /// <remarks>
    /// The server API takes the same JWT as a client, with a <c>roomList</c> grant instead of a
    /// join grant — and deliberately nothing else. This token can enumerate rooms and cannot enter
    /// one, so a leak of it discloses meeting sizes rather than meeting contents.
    /// </remarks>
    private HttpRequestMessage ServerApiRequest(string path, string body)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string token = Sign(
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["iss"] = _options.ApiKey,
                ["iat"] = now.ToUnixTimeSeconds(),
                ["nbf"] = now.ToUnixTimeSeconds(),
                ["exp"] = now.AddMinutes(1).ToUnixTimeSeconds(),
                ["video"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["roomList"] = true,
                    ["roomJoin"] = false,
                    ["roomCreate"] = false,
                },
            },
            _options.ApiSecret);

        string baseUrl = string.IsNullOrWhiteSpace(_options.ApiUrl)
            ? _options.Url.Replace("ws://", "http://", StringComparison.OrdinalIgnoreCase)
                .Replace("wss://", "https://", StringComparison.OrdinalIgnoreCase)
            : _options.ApiUrl;

        HttpRequestMessage request = new(HttpMethod.Post, new Uri(new Uri(baseUrl), path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    /// <summary>
    /// Signs an HS256 JWT.
    /// </summary>
    /// <remarks>
    /// Base64url without padding, per RFC 7515. The padding matters: a token with <c>=</c> in it is
    /// rejected by strict verifiers, and LiveKit is one.
    /// </remarks>
    private static string Sign(Dictionary<string, object> payload, string secret)
    {
        string header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["alg"] = "HS256", ["typ"] = "JWT" },
            SerializerOptions));

        string body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));

        string signingInput = $"{header}.{body}";

        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(secret));
        byte[] signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));

        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>One availability reading. Immutable, so it can be published by reference.</summary>
    private sealed record AvailabilityProbe(DateTimeOffset CheckedAt, bool Available);

    private sealed record RoomList(IReadOnlyList<Room>? Rooms);

    private sealed record Room(string Name, int NumParticipants);

    [LoggerMessage(EventId = 4401, Level = LogLevel.Warning, Message = "The LiveKit media host is unreachable; meetings will report unavailable while messaging continues")]
    private static partial void MediaHostUnreachable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4402, Level = LogLevel.Warning, Message = "LiveKit's server API returned {StatusCode}")]
    private static partial void ServerApiFailed(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 4403, Level = LogLevel.Warning, Message = "Could not read the platform-wide participant count; treating it as zero")]
    private static partial void CountUnavailable(ILogger logger, Exception exception);
}
