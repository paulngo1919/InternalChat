namespace InternalChat.Application.Abstractions;

/// <summary>
/// Drops cached authorization decisions when a membership changes.
/// </summary>
/// <remarks>
/// <para>
/// Split from <see cref="IMembershipReader"/>, which looks similar and answers a different
/// question. That one is the hot read path and is deliberately narrow; this one is the write-side
/// counterpart <c>AddMember</c>/<c>RemoveMember</c> call from inside their own transaction, per
/// Constitution Principle VII: "Cache invalidation MUST be part of the same use case that mutates
/// the underlying data."
/// </para>
/// <para>
/// By conversation, not by the one employee changed. A role change or a removal can alter what
/// other members' cached decisions should say too — for instance a removed admin's cached grants for
/// everyone else are unaffected, but enumerating exactly which cached entries a given change can
/// invalidate would make this a second source of truth about who is in the conversation. Dropping the
/// whole conversation's cache costs at most one PostgreSQL read per remaining member on their next
/// request, inside the 30-second TTL.
/// </para>
/// </remarks>
public interface IMembershipCacheInvalidator
{
    /// <summary>Drops every cached membership decision for a conversation.</summary>
    Task InvalidateConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
