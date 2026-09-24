namespace InternalChat.Api.Contracts;

/// <summary>A meeting (<c>Meeting</c> in openapi.yaml).</summary>
/// <param name="ParticipantCount">Who is in the room now, not who has ever been.</param>
/// <param name="MaxParticipants">
/// The per-meeting ceiling (FR-042). Served rather than assumed by the client, so a change to the
/// limit does not need a frontend deployment to be shown correctly.
/// </param>
public sealed record MeetingResponse(
    Guid Id,
    Guid ConversationId,
    Guid StartedBy,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int ParticipantCount,
    int MaxParticipants);

/// <summary>
/// A join credential (<c>MeetingToken</c> in openapi.yaml).
/// </summary>
/// <remarks>
/// <b>This one really is a bearer capability</b>, unlike an attachment's content URL. The media
/// server accepts it on its face and cannot re-check membership, which is why it is short-lived and
/// why <see cref="ExpiresAt"/> is served — a client that knows when it expires can request another
/// rather than failing to connect.
/// </remarks>
public sealed record MeetingTokenResponse(
    string Token,
    string MediaServerUrl,
    DateTimeOffset ExpiresAt);
