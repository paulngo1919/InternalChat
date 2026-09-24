using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Meetings;
using Microsoft.Extensions.Options;

namespace InternalChat.Api.Endpoints;

/// <summary>
/// T189 — the LiveKit webhook receiver that drives meeting lifecycle (FR-047, FR-051).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an anonymous endpoint, and the signature check is the only thing standing in front of
/// it.</b> LiveKit cannot present a user token, so the usual authorization pipeline does not apply.
/// Every claim in the body — who joined, which room, when it ended — is taken from an unauthenticated
/// request, and the entire trustworthiness of the audit record FR-051 requires rests on
/// <see cref="VerifySignature"/>. Forged webhooks would let anyone fabricate attendance.
/// </para>
/// <para>
/// <b>The signature is verified against the raw body, before deserialization.</b> Deserializing
/// first and re-serializing to check would compare a normalised form against a signature over the
/// original, which fails for harmless reasons and — worse — can be made to succeed for harmful ones.
/// </para>
/// <para>
/// <b>It always returns 200, even for events it ignores.</b> LiveKit retries a non-2xx, and
/// retrying an event this system deliberately does not handle is a loop that ends in its dead
/// letters rather than anything useful. Failures that deserve a retry are the ones that throw.
/// </para>
/// </remarks>
public static partial class MeetingWebhookEndpoints
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The route LiveKit is configured to POST to.</summary>
    public const string Path = "/webhooks/livekit";

    /// <summary>Maps the webhook receiver.</summary>
    public static IEndpointRouteBuilder MapMeetingWebhookEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost(Path, Receive)
            .WithTags("Meetings")
            .WithName("LiveKitWebhook")
            .WithSummary("LiveKit room lifecycle events, authenticated by signature")

            // Anonymous by necessity — LiveKit holds no user token. AllowAnonymous is explicit
            // rather than implied, because tests/Architecture/AuthorizationCoverageTests.cs fails
            // the build on an endpoint with no declared policy, and "no policy" must be a decision
            // somebody made rather than a line somebody forgot.
            .AllowAnonymous()

            // Excluded from the OpenAPI document: it is not part of the client contract, and
            // publishing it would invite a generated client to call it.
            .ExcludeFromDescription();

        return routes;
    }

    private static async Task<IResult> Receive(
        HttpRequest request,
        IUseCaseDispatcher dispatcher,
        IOptions<MeetingWebhookOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ILogger logger = loggerFactory.CreateLogger("InternalChat.Api.MeetingWebhook");

        using StreamReader reader = new(request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        string? authorization = request.Headers.Authorization.ToString();

        if (!VerifySignature(body, authorization, options.Value.SigningSecret))
        {
            // 401 and nothing else. No detail about why — a forged webhook's author should learn
            // nothing about the check that refused it.
            SignatureRejected(logger);
            return Results.Unauthorized();
        }

        LiveKitWebhookEvent? payload;

        try
        {
            payload = JsonSerializer.Deserialize<LiveKitWebhookEvent>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            // Signed but unreadable. A retry would produce the same unreadable body, so this is
            // accepted and dropped rather than retried forever.
            UnreadableBody(logger);
            return Results.Ok();
        }

        if (payload?.Event is null)
        {
            return Results.Ok();
        }

        await dispatcher
            .SendAsync<ApplyMediaSignal, bool>(
                new ApplyMediaSignal(
                    payload.Event,
                    ParseRoom(payload.Room?.Name),
                    ParseIdentity(payload.Participant?.Identity),
                    payload.Room?.NumParticipants ?? 0),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok();
    }

    /// <summary>
    /// Verifies LiveKit's webhook signature.
    /// </summary>
    /// <remarks>
    /// <para>
    /// LiveKit signs with a JWT whose <c>sha256</c> claim is the base64 SHA-256 of the request body,
    /// carried in the <c>Authorization</c> header. Two things must hold: the JWT's own HMAC must
    /// verify against the API secret, and its body digest must match the bytes actually received.
    /// Checking only the first would accept a valid token replayed over a different body.
    /// </para>
    /// <para>
    /// Comparisons are fixed-time. A byte-by-byte early exit on an HMAC comparison leaks the
    /// signature one byte at a time to anyone willing to measure, which is a textbook forgery path.
    /// </para>
    /// </remarks>
    internal static bool VerifySignature(string body, string? authorization, string apiSecret)
    {
        if (string.IsNullOrWhiteSpace(authorization) || string.IsNullOrWhiteSpace(apiSecret))
        {
            return false;
        }

        string token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : authorization.Trim();

        string[] parts = token.Split('.');

        if (parts.Length != 3)
        {
            return false;
        }

        // 1. The token's own signature.
        using HMACSHA256 hmac = new(Encoding.UTF8.GetBytes(apiSecret));
        byte[] expected = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"));

        byte[] actual;

        try
        {
            actual = FromBase64Url(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return false;
        }

        // 2. The digest of the body it claims to cover.
        try
        {
            using JsonDocument claims = JsonDocument.Parse(FromBase64Url(parts[1]));

            if (!claims.RootElement.TryGetProperty("sha256", out JsonElement digestClaim)
                || digestClaim.GetString() is not { } declaredDigest)
            {
                // A valid token with no body digest covers nothing. Refused rather than treated as
                // "signature fine" — that would accept any body under a replayed token.
                return false;
            }

            byte[] bodyDigest = SHA256.HashData(Encoding.UTF8.GetBytes(body));

            return CryptographicOperations.FixedTimeEquals(
                bodyDigest, Convert.FromBase64String(declaredDigest));
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return false;
        }
    }

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    /// <summary>The room name is the meeting id (data-model.md).</summary>
    private static Guid? ParseRoom(string? name) => Guid.TryParse(name, out Guid id) ? id : null;

    /// <summary>The participant identity is the employee id, set when the token was minted.</summary>
    private static Guid? ParseIdentity(string? identity) =>
        Guid.TryParse(identity, out Guid id) ? id : null;

    [LoggerMessage(EventId = 4501, Level = LogLevel.Warning, Message = "A LiveKit webhook failed signature verification and was discarded")]
    private static partial void SignatureRejected(ILogger logger);

    [LoggerMessage(EventId = 4502, Level = LogLevel.Warning, Message = "A signed LiveKit webhook carried an unreadable body")]
    private static partial void UnreadableBody(ILogger logger);

    private sealed record LiveKitWebhookEvent(
        string? Event,
        LiveKitRoom? Room,
        LiveKitParticipant? Participant);

    private sealed record LiveKitRoom(string? Name, int NumParticipants);

    private sealed record LiveKitParticipant(string? Identity);
}

/// <summary>The secret LiveKit signs its webhooks with.</summary>
/// <remarks>
/// <para>
/// Declared in the Api project rather than reusing Infrastructure's <c>LiveKitOptions</c>, and not
/// out of tidiness: Constitution Principle I forbids this project naming an Infrastructure type
/// anywhere but <c>Program.cs</c>, and <c>tests/Architecture/CompositionRootTests.cs</c> fails the
/// build over it. Both bind the same configured value.
/// </para>
/// <para>
/// It is the same secret that signs join tokens, because LiveKit uses one. That makes it the single
/// most sensitive value in the deployment: it mints admission to any meeting and authenticates every
/// statement this system records about who attended one.
/// </para>
/// </remarks>
public sealed class MeetingWebhookOptions
{
    /// <summary>Configuration section name. Shares LiveKit's section so one value configures both.</summary>
    public const string SectionName = "LiveKit";

    /// <summary>The shared secret. Bound from <c>LiveKit:ApiSecret</c>.</summary>
    public string SigningSecret { get; set; } = string.Empty;
}
