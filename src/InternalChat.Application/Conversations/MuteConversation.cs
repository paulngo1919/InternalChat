using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Conversations;

namespace InternalChat.Application.Conversations;

/// <summary>
/// Mutes or unmutes a conversation for the caller (FR-037).
/// </summary>
/// <remarks>
/// <para>
/// <b>A contract gap this fills.</b> data-model.md's <c>membership.muted_until</c> and
/// <c>Membership.MuteUntil</c> (T059) both exist, and openapi.yaml's <c>Conversation.mutedUntil</c>
/// documents the read side — but no path was ever specified to set it. FR-037 requires one, so this
/// use case and its endpoint (<c>PUT /conversations/{id}/mute</c>) are added now, and openapi.yaml
/// is corrected alongside rather than left describing a field nothing can write.
/// </para>
/// <para>
/// No cache invalidation and no audit entry. <see cref="Caching.MembershipCache"/>'s cached
/// decision carries only <c>Role</c> and <c>VisibleFromSeq</c> (see its <c>CachedDecision</c>
/// remarks) — mute is not an access decision, so there is nothing stale to invalidate. It is
/// likewise absent from the constitution's audit list: a personal notification preference, not a
/// security-relevant action.
/// </para>
/// </remarks>
public sealed record MuteConversation(Guid ConversationId, Guid EmployeeId, DateTimeOffset? MutedUntil)
    : ITransactionalRequest;

/// <summary>Sets or clears the caller's own mute for one conversation.</summary>
public sealed class MuteConversationHandler : IUseCase<MuteConversation, Membership>
{
    private readonly IMembershipRepository _memberships;

    /// <summary>Creates the handler.</summary>
    public MuteConversationHandler(IMembershipRepository memberships)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        _memberships = memberships;
    }

    /// <inheritdoc />
    public async Task<Membership> HandleAsync(MuteConversation request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Membership membership = await _memberships
            .FindAsync(request.ConversationId, request.EmployeeId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Employee {request.EmployeeId} is not a member of conversation {request.ConversationId}.");

        membership.MuteUntil(request.MutedUntil);

        return membership;
    }
}
