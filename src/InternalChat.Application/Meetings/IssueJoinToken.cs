using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Meetings;

namespace InternalChat.Application.Meetings;

/// <summary>Requests admission to a meeting (FR-041).</summary>
public sealed record IssueJoinToken(Guid MeetingId, Guid RequestedBy);

/// <summary>The credential and where to use it.</summary>
public sealed record JoinTicket(Meeting Meeting, MeetingAccessToken Token);

/// <summary>
/// Performs the membership check that IS the meeting access control (T188, FR-041).
/// </summary>
/// <remarks>
/// <para>
/// <b>This class is the entire meeting security boundary.</b> The media server trusts its token
/// completely — it runs no membership check of its own, and it cannot, because it knows nothing
/// about conversations. FR-041's "only members of that conversation can join" is enforced here, by
/// this handler declining to mint a token, and nowhere else. There is no second layer to catch a
/// mistake made in this file.
/// </para>
/// <para>
/// <b>The conversation comes from the meeting row, never from the request.</b> A caller supplies
/// only a meeting id; the conversation it belongs to is read from the row and that is what
/// membership is checked against. Accepting a conversation id from the caller would let someone
/// nominate a conversation they happen to belong to and be admitted to a meeting in a different one.
/// </para>
/// <para>
/// <b>The token is short-lived because it is a bearer capability.</b> Unlike an attachment URL —
/// which is a route that re-checks membership on every request — this really is a credential the
/// media server accepts on its face. It cannot be re-checked, so the only control available is
/// making the window small: long enough to connect, short enough that a leaked token is worthless
/// by the time anyone finds it. Revocation within the window is not possible and is not claimed;
/// FR-030's five-minute bound is met because the token cannot outlive it.
/// </para>
/// </remarks>
public sealed class IssueJoinTokenHandler : IUseCase<IssueJoinToken, JoinTicket>
{
    /// <summary>
    /// How long a join token is accepted.
    /// </summary>
    /// <remarks>
    /// Five minutes: comfortably longer than any connection handshake, and equal to FR-030's
    /// revocation bound, so a token issued to someone whose access ends immediately afterwards
    /// expires no later than the deadline that promise sets.
    /// </remarks>
    public static TimeSpan TokenLifetime => TimeSpan.FromMinutes(5);

    private readonly IMeetingRepository _meetings;
    private readonly IMembershipReader _memberships;
    private readonly IEmployeeStore _employees;
    private readonly IMeetingTokenIssuer _issuer;
    private readonly IMeetingCapacityGuard _capacity;

    /// <summary>Creates the handler.</summary>
    public IssueJoinTokenHandler(
        IMeetingRepository meetings,
        IMembershipReader memberships,
        IEmployeeStore employees,
        IMeetingTokenIssuer issuer,
        IMeetingCapacityGuard capacity)
    {
        ArgumentNullException.ThrowIfNull(meetings);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(capacity);

        _meetings = meetings;
        _memberships = memberships;
        _employees = employees;
        _issuer = issuer;
        _capacity = capacity;
    }

    /// <inheritdoc />
    public async Task<JoinTicket> HandleAsync(
        IssueJoinToken request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Meeting? meeting = await _meetings
            .FindAsync(request.MeetingId, cancellationToken)
            .ConfigureAwait(false);

        if (meeting is null || !meeting.IsActive)
        {
            // An ended meeting and one that never existed are the same refusal. Distinguishing them
            // would confirm that a meeting id is real, which is the enumeration answer every other
            // resource here also declines to give.
            throw Refuse(request.MeetingId);
        }

        // THE check. Read from the meeting's own conversation, not from anything the caller sent.
        MembershipSnapshot? membership = await _memberships
            .FindGrantingAsync(meeting.ConversationId, request.RequestedBy, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            throw Refuse(request.MeetingId);
        }

        // Only now, with the caller established as a member, is it safe to say anything specific:
        // everything below describes the state of a meeting they are entitled to know about.
        if (!meeting.HasCapacity)
        {
            throw new MeetingFullException(meeting.Id, Meeting.MaximumParticipants);
        }

        if (!await _capacity.HasCapacityAsync(1, cancellationToken).ConfigureAwait(false))
        {
            throw new PlatformAtCapacityException(
                await _capacity.GetCountAsync(cancellationToken).ConfigureAwait(false));
        }

        // From the directory, never from the client. A display name supplied by the joiner would
        // let anyone appear on a tile under a colleague's name, which is impersonation the media
        // server has no way to detect — it trusts whatever the token says.
        IReadOnlyList<Domain.Employees.Employee> found = await _employees
            .FindManyAsync([request.RequestedBy], cancellationToken)
            .ConfigureAwait(false);

        Domain.Employees.Employee? employee = found.Count > 0 ? found[0] : null;

        MeetingAccessToken token = await _issuer
            .IssueAsync(
                meeting.Id,
                request.RequestedBy,
                employee?.DisplayName ?? "Unknown",
                TokenLifetime,
                cancellationToken)
            .ConfigureAwait(false);

        // The participation row is NOT written here. Someone who asks for a token has not joined —
        // they may close the tab. LiveKit's participant_joined webhook is what records arrival, and
        // it is the only signal that means somebody is actually in the room (FR-051).
        return new JoinTicket(meeting, token);
    }

    private static UnauthorizedAccessException Refuse(Guid meetingId) =>
        new($"Meeting {meetingId} is not accessible.");
}
