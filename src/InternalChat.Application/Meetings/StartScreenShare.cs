using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Meetings;

namespace InternalChat.Application.Meetings;

/// <summary>Claims the screen-share slot in a meeting (FR-048, FR-050).</summary>
/// <param name="Scope">Whether the whole screen or a single window is being shared.</param>
public sealed record StartScreenShare(
    Guid MeetingId,
    Guid RequestedBy,
    ShareScope Scope) : ITransactionalRequest;

/// <summary>Releases the screen-share slot.</summary>
public sealed record StopScreenShare(Guid MeetingId, Guid RequestedBy) : ITransactionalRequest;

/// <summary>Who holds the slot, and who was displaced to give it to them.</summary>
/// <param name="Displaced">
/// The participant whose share was stopped by this one, or <c>null</c> when the slot was free.
/// The caller announces it — FR-050 requires the rule to be <em>visible</em>, and the displaced
/// person is the one who most needs to see it.
/// </param>
public sealed record ScreenShareClaim(ShareSession Session, Guid? Displaced);

/// <summary>
/// Enforces one screen-share publisher per meeting, server-side (T201, FR-050).
/// </summary>
/// <remarks>
/// <para>
/// <b>Server-side is the point of this class.</b> The browser will happily let two people publish
/// a screen-share track at once, and LiveKit will forward both — producing a meeting with two
/// shared screens, which is neither of the outcomes FR-050 permits. The rule has to live somewhere
/// both clients agree on, and that is here.
/// </para>
/// <para>
/// <b>The rule is last-writer-wins, and it is a choice rather than a derivation.</b> FR-050 asks
/// only for "one defined, visible rule". Refusing the second sharer reads as more polite and
/// behaves worse in the case that actually happens: someone is presenting, the meeting moves on,
/// and the next person cannot take over until the first notices. That produces a meeting where
/// people ask each other to stop sharing, which is the friction the requirement exists to remove.
/// </para>
/// <para>
/// <b>The displaced participant is returned, not just logged.</b> "Visible" is the operative word
/// in FR-050: the person whose share just vanished needs to be told it was taken over rather than
/// left wondering whether their connection dropped.
/// </para>
/// <para>
/// <b>Membership is re-checked here even though the participant is already in the meeting.</b>
/// Their join token was minted up to five minutes ago and cannot be revoked; this is a fresh
/// database-backed decision, and it is the one that honours FR-030 for someone whose access ended
/// mid-call.
/// </para>
/// </remarks>
public sealed class StartScreenShareHandler : IUseCase<StartScreenShare, ScreenShareClaim>
{
    private readonly IMeetingRepository _meetings;
    private readonly IShareSessionStore _shares;
    private readonly IMembershipReader _memberships;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public StartScreenShareHandler(
        IMeetingRepository meetings,
        IShareSessionStore shares,
        IMembershipReader memberships,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(meetings);
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(clock);

        _meetings = meetings;
        _shares = shares;
        _memberships = memberships;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ScreenShareClaim> HandleAsync(
        StartScreenShare request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Meeting meeting = await AuthorizeAsync(
            request.MeetingId, request.RequestedBy, cancellationToken).ConfigureAwait(false);

        ShareSession? current = await _shares
            .FindActiveAsync(meeting.Id, cancellationToken)
            .ConfigureAwait(false);

        if (current is not null && current.EmployeeId == request.RequestedBy)
        {
            // Already sharing. Idempotent rather than a takeover of themselves — a client that
            // re-sends the claim on reconnect must not produce a Superseded record naming the
            // person as both parties.
            return new ScreenShareClaim(current, Displaced: null);
        }

        Guid? displaced = null;

        if (current is not null)
        {
            // Stopped BEFORE the new one starts, so there is no instant at which the store holds
            // two active sessions. A reader between the two writes would otherwise see exactly the
            // state FR-050 forbids.
            current.Stop(ShareStopReason.Superseded, _clock);
            await _shares.UpdateAsync(current, cancellationToken).ConfigureAwait(false);

            displaced = current.EmployeeId;
        }

        ShareSession session = ShareSession.Start(
            meeting.Id, request.RequestedBy, request.Scope, _clock);

        await _shares.AddAsync(session, cancellationToken).ConfigureAwait(false);

        return new ScreenShareClaim(session, displaced);
    }

    /// <summary>
    /// Confirms the meeting is live and the caller may still be in it.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="StopScreenShareHandler"/>. The refusals are identical to every other
    /// meeting path, so sharing cannot become the one place that distinguishes "no such meeting"
    /// from "not yours".
    /// </remarks>
    internal static async Task<Meeting> AuthorizeCoreAsync(
        IMeetingRepository meetings,
        IMembershipReader memberships,
        Guid meetingId,
        Guid requestedBy,
        CancellationToken cancellationToken)
    {
        Meeting? meeting = await meetings.FindAsync(meetingId, cancellationToken).ConfigureAwait(false);

        if (meeting is null || !meeting.IsActive)
        {
            throw new UnauthorizedAccessException($"Meeting {meetingId} is not accessible.");
        }

        MembershipSnapshot? membership = await memberships
            .FindGrantingAsync(meeting.ConversationId, requestedBy, cancellationToken)
            .ConfigureAwait(false);

        return membership is null
            ? throw new UnauthorizedAccessException($"Meeting {meetingId} is not accessible.")
            : meeting;
    }

    private Task<Meeting> AuthorizeAsync(
        Guid meetingId,
        Guid requestedBy,
        CancellationToken cancellationToken) =>
        AuthorizeCoreAsync(_meetings, _memberships, meetingId, requestedBy, cancellationToken);
}

/// <summary>Releases the slot, if the caller holds it.</summary>
/// <remarks>
/// Silently does nothing when the caller is not the current sharer. That is the common case rather
/// than an error: a participant who was superseded still sends a stop when they close their picker,
/// and turning that into a failure would produce an error nobody caused and nobody can act on.
/// </remarks>
public sealed class StopScreenShareHandler : IUseCase<StopScreenShare, bool>
{
    private readonly IMeetingRepository _meetings;
    private readonly IShareSessionStore _shares;
    private readonly IMembershipReader _memberships;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public StopScreenShareHandler(
        IMeetingRepository meetings,
        IShareSessionStore shares,
        IMembershipReader memberships,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(meetings);
        ArgumentNullException.ThrowIfNull(shares);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(clock);

        _meetings = meetings;
        _shares = shares;
        _memberships = memberships;
        _clock = clock;
    }

    /// <inheritdoc />
    /// <returns><c>true</c> when a share was actually stopped.</returns>
    public async Task<bool> HandleAsync(
        StopScreenShare request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Meeting meeting = await StartScreenShareHandler
            .AuthorizeCoreAsync(_meetings, _memberships, request.MeetingId, request.RequestedBy, cancellationToken)
            .ConfigureAwait(false);

        ShareSession? current = await _shares
            .FindActiveAsync(meeting.Id, cancellationToken)
            .ConfigureAwait(false);

        if (current is null || current.EmployeeId != request.RequestedBy)
        {
            return false;
        }

        current.Stop(ShareStopReason.Stopped, _clock);
        await _shares.UpdateAsync(current, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
