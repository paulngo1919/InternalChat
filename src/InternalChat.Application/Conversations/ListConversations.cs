using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Conversations;

namespace InternalChat.Application.Conversations;

/// <summary>Lists the conversations an employee belongs to, newest activity first.</summary>
/// <param name="Cursor">
/// The <c>updatedAt</c> of the last row of the previous page, exclusive. <c>null</c> starts at the
/// newest.
/// </param>
public sealed record ListConversations(Guid EmployeeId, DateTimeOffset? Cursor, int Limit);

/// <summary>One page of conversations.</summary>
public sealed record ConversationPage(IReadOnlyList<ConversationSummary> Items, DateTimeOffset? NextCursor);

/// <summary>
/// Reads the caller's conversation list.
/// </summary>
/// <remarks>
/// <para>
/// Scoped by membership in the query itself, so there is no filtering step that could be forgotten
/// and no id from the request to tamper with — the employee id comes from the resolved identity.
/// </para>
/// <para>
/// Ordered by <c>updated_at</c> rather than by conversation id or creation time, because a chat list
/// that did not move when a message arrived would be useless. The send path advances
/// <c>updated_at</c> in the same <c>UPDATE</c> that allocates the sequence, so the ordering is a
/// consequence of activity rather than a second thing to remember to maintain.
/// </para>
/// </remarks>
public sealed class ListConversationsHandler : IUseCase<ListConversations, ConversationPage>
{
    /// <summary>Largest page the API will serve, per the OpenAPI document.</summary>
    public const int MaximumLimit = 100;

    /// <summary>Page size when the caller does not ask for one.</summary>
    public const int DefaultLimit = 50;

    private readonly IConversationReader _conversations;

    /// <summary>Creates the handler.</summary>
    public ListConversationsHandler(IConversationReader conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        _conversations = conversations;
    }

    /// <inheritdoc />
    public async Task<ConversationPage> HandleAsync(
        ListConversations request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        int limit = Math.Clamp(request.Limit <= 0 ? DefaultLimit : request.Limit, 1, MaximumLimit);

        IReadOnlyList<ConversationSummary> items = await _conversations
            .ListForEmployeeAsync(request.EmployeeId, limit + 1, request.Cursor, cancellationToken)
            .ConfigureAwait(false);

        bool hasMore = items.Count > limit;
        IReadOnlyList<ConversationSummary> page = hasMore ? [.. items.Take(limit)] : items;

        DateTimeOffset? nextCursor = hasMore && page.Count > 0
            ? page[^1].Conversation.UpdatedAt
            : null;

        return new ConversationPage(page, nextCursor);
    }
}

/// <summary>Reads one conversation the caller belongs to.</summary>
public sealed record GetConversation(Guid ConversationId, Guid EmployeeId);

/// <summary>
/// Serves conversation detail.
/// </summary>
/// <remarks>
/// The membership filter has already refused non-members, so this loads without re-checking. A
/// missing row is shaped as a refusal rather than a plain not-found, because the two must be
/// indistinguishable (SC-017) and the filter's own refusal takes that shape.
/// </remarks>
public sealed class GetConversationHandler : IUseCase<GetConversation, ConversationSummary>
{
    private readonly IConversationReader _conversations;

    /// <summary>Creates the handler.</summary>
    public GetConversationHandler(IConversationReader conversations)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        _conversations = conversations;
    }

    /// <inheritdoc />
    public async Task<ConversationSummary> HandleAsync(
        GetConversation request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await _conversations
            .FindForEmployeeAsync(request.ConversationId, request.EmployeeId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Conversation {request.ConversationId} is not reachable by employee {request.EmployeeId}.");
    }
}
