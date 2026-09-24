using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Messages;

namespace InternalChat.Application.Messages;

/// <summary>Reads a page of conversation history (FR-010, FR-013).</summary>
/// <param name="BeforeSeq">Page backwards from here, exclusive. Newest first.</param>
/// <param name="AfterSeq">Catch up from here, exclusive. Oldest first.</param>
public sealed record GetHistory(
    Guid ConversationId,
    Guid EmployeeId,
    long? BeforeSeq,
    long? AfterSeq,
    int Limit);

/// <summary>One page of history.</summary>
/// <param name="HasMore">
/// A fact, not an inference. Derived by asking the database for one row more than the page size and
/// discarding it — the obvious client-side substitute ("a full page means there is probably
/// another") is wrong exactly at the boundary, where the last page is full and the client waits
/// forever for messages that do not exist.
/// </param>
/// <param name="Attachments">
/// The attachments carried by the messages on this page, keyed by message id. Loaded in one batched
/// query rather than per message: a page is 50 messages, and a query each would break the
/// per-request budget <c>QueryCountInterceptor</c> enforces well before it broke the 250 ms target.
/// </param>
public sealed record HistoryPage(
    IReadOnlyList<Message> Messages,
    long? NextCursor,
    bool HasMore,
    IReadOnlyDictionary<Guid, IReadOnlyList<Domain.Attachments.Attachment>> Attachments);

/// <summary>
/// Serves keyset-paginated history, floored at the caller's own visibility.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyset, not offset.</b> An <c>OFFSET</c> re-reads and re-counts rows on every page, so it gets
/// slower the further back someone scrolls — against a 250 ms budget on a table holding 125 million
/// rows. Worse, it is wrong while the conversation is live: a message arriving mid-scroll shifts
/// every subsequent offset by one, so the reader silently skips a message or sees one twice.
/// </para>
/// <para>
/// <b>The floor is applied in SQL, not afterwards.</b> Filtering a fetched page in memory would make
/// the returned count depend on how much of it the caller may see, so a member with a high floor
/// would get short pages and eventually an empty one — which the client cannot distinguish from the
/// start of the conversation.
/// </para>
/// </remarks>
public sealed class GetHistoryHandler : IUseCase<GetHistory, HistoryPage>
{
    /// <summary>Largest page the API will serve, per Principle V and the OpenAPI document.</summary>
    public const int MaximumLimit = 100;

    /// <summary>Page size when the caller does not ask for one.</summary>
    public const int DefaultLimit = 50;

    private readonly IMessageRepository _messages;
    private readonly IMembershipRepository _memberships;
    private readonly IAttachmentRepository _attachments;

    /// <summary>Creates the handler.</summary>
    public GetHistoryHandler(
        IMessageRepository messages,
        IMembershipRepository memberships,
        IAttachmentRepository attachments)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(attachments);

        _messages = messages;
        _memberships = memberships;
        _attachments = attachments;
    }

    /// <inheritdoc />
    public async Task<HistoryPage> HandleAsync(
        GetHistory request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read here rather than taken from the membership filter's cached decision. The filter
        // answers "may this caller reach the conversation" and is cached for 30 seconds; the floor
        // decides what they are shown, and serving that from a stale cache would show a re-added
        // member history they were removed from for up to half a minute.
        Membership? membership = await _memberships
            .FindAsync(request.ConversationId, request.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null || !membership.IsActive)
        {
            // Shaped as a refusal so it is indistinguishable from a conversation that does not
            // exist (SC-017). The filter should already have refused; this is what happens if
            // membership ended between the filter and here.
            throw new UnauthorizedAccessException(
                $"No active membership for conversation {request.ConversationId}.");
        }

        int limit = Math.Clamp(request.Limit <= 0 ? DefaultLimit : request.Limit, 1, MaximumLimit);

        // One extra row, discarded below. This is what makes HasMore a fact.
        IReadOnlyList<Message> fetched = await _messages
            .GetHistoryAsync(
                new MessageHistoryQuery(
                    request.ConversationId,
                    membership.VisibleFromSeq,
                    request.BeforeSeq,
                    request.AfterSeq,
                    limit + 1),
                cancellationToken)
            .ConfigureAwait(false);

        bool hasMore = fetched.Count > limit;
        IReadOnlyList<Message> page = hasMore ? [.. fetched.Take(limit)] : fetched;

        // The cursor is the sequence the next request continues from, which is the last row of this
        // page in whichever direction it was read. Null when there is nothing further, so a client
        // stops rather than re-requesting the same page forever.
        long? nextCursor = hasMore && page.Count > 0 ? page[^1].Seq : null;

        // Batched over the whole page, and only when the page is non-empty. A text-only
        // conversation — the overwhelming majority — pays nothing for this.
        IReadOnlyList<Domain.Attachments.Attachment> attachments = await _attachments
            .GetForMessagesAsync([.. page.Select(m => m.Id)], cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, IReadOnlyList<Domain.Attachments.Attachment>> byMessage = attachments
            .GroupBy(a => a.MessageId!.Value)
            .ToDictionary(g => g.Key, IReadOnlyList<Domain.Attachments.Attachment> (g) => [.. g]);

        return new HistoryPage(page, nextCursor, hasMore, byMessage);
    }
}

/// <summary>Rejects a history request that could not be served sensibly.</summary>
public sealed class GetHistoryValidator : IValidator<GetHistory>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(
        GetHistory request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ValidationError> errors = [];

        if (request.BeforeSeq is not null && request.AfterSeq is not null)
        {
            // Two bounds in opposite directions have no single correct ordering, and picking one
            // silently would make the other parameter appear to be ignored.
            errors.Add(new ValidationError(
                "beforeSeq",
                "Use beforeSeq to page backwards or afterSeq to catch up, not both."));
        }

        if (request.BeforeSeq < 0)
        {
            errors.Add(new ValidationError("beforeSeq", "A sequence is not negative."));
        }

        if (request.AfterSeq < 0)
        {
            errors.Add(new ValidationError("afterSeq", "A sequence is not negative."));
        }

        return ValueTask.FromResult(errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors));
    }
}
