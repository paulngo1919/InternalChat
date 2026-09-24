using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Notifications;

namespace InternalChat.Application.Notifications;

/// <summary>Advances a read position, monotonically (FR-036).</summary>
public sealed record MarkRead(Guid EmployeeId, Guid ConversationId, long LastReadSeq) : ITransactionalRequest;

/// <summary>The effective read state after the merge (<c>ReadState</c> in openapi.yaml).</summary>
public sealed record MarkReadResult(Guid ConversationId, long LastReadSeq, int UnreadCount);

/// <summary>
/// Merges a reported read position and, when it actually advanced, publishes
/// <c>chat.read_state.updated.v1</c> so the employee's other devices clear the same badge.
/// </summary>
/// <remarks>
/// Membership, not just an id, is required to compute the response: <c>unreadCount</c> is
/// <c>last_seq - GREATEST(last_read_seq, visible_from_seq)</c> (data-model.md), and
/// <c>visible_from_seq</c> is what stops a member's unread count from including messages sent
/// before they joined.
/// </remarks>
public sealed class MarkReadHandler : IUseCase<MarkRead, MarkReadResult>
{
    private readonly IReadStateRepository _readStates;
    private readonly IConversationRepository _conversations;
    private readonly IMembershipRepository _memberships;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public MarkReadHandler(
        IReadStateRepository readStates,
        IConversationRepository conversations,
        IMembershipRepository memberships,
        IEventPublisher events,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(readStates);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _readStates = readStates;
        _conversations = conversations;
        _memberships = memberships;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<MarkReadResult> HandleAsync(MarkRead request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The membership filter has already confirmed the caller belongs here; a missing row means
        // it was removed between the two — refused, not applied.
        Membership membership = await _memberships
            .FindAsync(request.ConversationId, request.EmployeeId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Employee {request.EmployeeId} is not a member of conversation {request.ConversationId}.");

        Conversation conversation = await _conversations
            .FindAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Conversation {request.ConversationId} does not exist.");

        ReadState? state = await _readStates
            .FindAsync(request.EmployeeId, request.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        bool advanced;

        if (state is null)
        {
            state = ReadState.Start(request.EmployeeId, request.ConversationId, request.LastReadSeq, _clock);
            await _readStates.AddAsync(state, cancellationToken).ConfigureAwait(false);
            advanced = request.LastReadSeq > 0;
        }
        else
        {
            advanced = state.AdvanceTo(request.LastReadSeq, _clock);
        }

        if (advanced)
        {
            await _events.PublishAsync(
                new ReadStateUpdated(
                    Guid.CreateVersion7(), _clock.UtcNow, request.EmployeeId, request.ConversationId, state.LastReadSeq),
                cancellationToken).ConfigureAwait(false);
        }

        long floor = Math.Max(state.LastReadSeq, membership.VisibleFromSeq);
        int unread = (int)Math.Max(0, conversation.LastSeq - floor);

        return new MarkReadResult(request.ConversationId, state.LastReadSeq, unread);
    }
}

/// <summary>Rejects a read report before it can write anything.</summary>
public sealed class MarkReadValidator : IValidator<MarkRead>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(MarkRead request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ValueTask.FromResult(
            request.LastReadSeq < 0
                ? ValidationResult.Fail("lastReadSeq", "A read position cannot be negative.")
                : ValidationResult.Valid);
    }
}
