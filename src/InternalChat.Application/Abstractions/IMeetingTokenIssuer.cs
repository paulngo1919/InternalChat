namespace InternalChat.Application.Abstractions;

/// <summary>A short-lived credential admitting one employee to one meeting room.</summary>
/// <param name="Token">The signed join token.</param>
/// <param name="MediaServerUrl">Where the client connects.</param>
/// <param name="ExpiresAt">When the token stops being accepted.</param>
public sealed record MeetingAccessToken(string Token, Uri MediaServerUrl, DateTimeOffset ExpiresAt);

/// <summary>
/// Mints join tokens for the media server.
/// </summary>
/// <remarks>
/// <para>
/// This is the meeting security boundary, and the only one. The media server trusts its token
/// completely — it performs no membership check of its own — so FR-041's "only members of that
/// conversation can join" is enforced entirely by the caller refusing to mint a token. Every
/// implementation and caller of this interface is on the security sign-off list in plan.md.
/// </para>
/// <para>
/// <see cref="IsAvailableAsync"/> exists because the media host is the one component running on
/// a second machine. Constitution v1.2.0 requires the application to stay fully functional
/// without it: messaging, attachments, search, and notifications must keep working while
/// meetings report unavailable. Callers check availability rather than discovering it through a
/// timeout on the request path.
/// </para>
/// </remarks>
public interface IMeetingTokenIssuer
{
    /// <summary>
    /// Issues a join token. Callers MUST verify conversation membership first — this method
    /// does not, and cannot, check it.
    /// </summary>
    Task<MeetingAccessToken> IssueAsync(
        Guid meetingId,
        Guid employeeId,
        string displayName,
        TimeSpan validFor,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the media host is reachable, so the API can report meetings unavailable instead
    /// of degrading into an outage.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Current platform-wide participant count, for the FR-043 capacity ceiling.</summary>
    Task<int> GetActiveParticipantCountAsync(CancellationToken cancellationToken = default);
}
