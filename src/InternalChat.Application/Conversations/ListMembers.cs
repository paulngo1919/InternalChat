using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;

namespace InternalChat.Application.Conversations;

/// <summary>Lists the active members of a conversation the caller belongs to.</summary>
public sealed record ListMembers(Guid ConversationId);

/// <summary>
/// Reads a conversation's member list.
/// </summary>
/// <remarks>
/// Authorization is the endpoint filter's job — the same membership check every conversation-scoped
/// route applies. This handler does not re-check it, for the same reason
/// <see cref="GetConversationHandler"/> does not: a second, independent implementation of "is the
/// caller allowed to see this" is a second place for the two to disagree.
/// </remarks>
public sealed class ListMembersHandler : IUseCase<ListMembers, IReadOnlyList<MemberProjection>>
{
    private readonly IConversationReader _conversations;

    /// <summary>Creates the handler.</summary>
    public ListMembersHandler(IConversationReader conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        _conversations = conversations;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MemberProjection>> HandleAsync(
        ListMembers request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _conversations.ListMembersAsync(request.ConversationId, cancellationToken);
    }
}
