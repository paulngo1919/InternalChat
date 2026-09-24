using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;

namespace InternalChat.Application.Conversations;

/// <summary>Removes a member from a group conversation (FR-008, US3 scenario 3).</summary>
public sealed record RemoveMember(
    Guid ConversationId,
    Guid ActorId,
    Guid EmployeeId) : ITransactionalRequest, IAuditableRequest
{
    /// <inheritdoc />
    public AuditEntry ToAuditEntry(AuditOutcome outcome) => new(
        "membership.removed",
        ActorId,
        "conversation",
        ConversationId,
        SourceIp: null,
        outcome,
        new Dictionary<string, string>(StringComparer.Ordinal) { ["employeeId"] = EmployeeId.ToString() });
}

/// <summary>
/// Withdraws access immediately — for new messages, history, and (later) attachments and search.
/// </summary>
/// <remarks>
/// <para>
/// Idempotent by construction: <see cref="Membership.Remove"/> never moves
/// <see cref="Membership.RemovedAt"/> on a second call, so a retried request and a genuine repeat
/// removal both leave the audit trail's timestamp exactly where it was set the first time.
/// </para>
/// <para>
/// Cache invalidation happens whether or not the row was already removed. A membership already
/// absent from the cache costs nothing extra to invalidate again, and skipping it on the "already
/// removed" path would be one more case to get right for no benefit.
/// </para>
/// </remarks>
public sealed class RemoveMemberHandler : IUseCase<RemoveMember, Membership>
{
    private readonly IConversationRepository _conversations;
    private readonly IMembershipRepository _memberships;
    private readonly IMembershipCacheInvalidator _cache;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public RemoveMemberHandler(
        IConversationRepository conversations,
        IMembershipRepository memberships,
        IMembershipCacheInvalidator cache,
        IEventPublisher events,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _conversations = conversations;
        _memberships = memberships;
        _cache = cache;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Membership> HandleAsync(RemoveMember request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Conversation conversation = await _conversations
            .FindAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Conversation {request.ConversationId} does not exist.");

        conversation.EnsureMembersMayChange();

        Membership membership = await _memberships
            .FindAsync(request.ConversationId, request.EmployeeId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Employee {request.EmployeeId} is not a member of conversation {request.ConversationId}.");

        membership.Remove(_clock);

        await _cache.InvalidateConversationAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);

        await _events.PublishAsync(
            MembershipChanged.Create(
                request.ConversationId,
                request.EmployeeId,
                MembershipChangeKind.Removed,
                request.ActorId,
                membership.VisibleFromSeq,
                _clock),
            cancellationToken).ConfigureAwait(false);

        return membership;
    }
}
