using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Messages;

namespace InternalChat.Application.Messages;

/// <summary>Edits one's own message inside the 24-hour window (FR-014).</summary>
public sealed record EditMessage(
    Guid ConversationId,
    Guid MessageId,
    Guid EditorId,
    string Body) : ITransactionalRequest;

/// <summary>Deletes one's own message inside the 24-hour window (FR-014).</summary>
public sealed record DeleteMessage(
    Guid ConversationId,
    Guid MessageId,
    Guid ActorId) : ITransactionalRequest;

/// <summary>
/// Replaces a message body, leaving its identity and its place in the order untouched.
/// </summary>
/// <remarks>
/// Every rule here — author only, within 24 hours, not already deleted — lives on
/// <see cref="Message.Edit"/> rather than in this handler. That is what keeps the edit endpoint and
/// any future path (an admin correction, a bulk redaction) from each enforcing its own idea of the
/// window. The handler's job is to load the right row and publish the event.
/// </remarks>
public sealed class EditMessageHandler : IUseCase<EditMessage, Message>
{
    private readonly IMessageRepository _messages;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public EditMessageHandler(IMessageRepository messages, IEventPublisher events, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _messages = messages;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Message> HandleAsync(
        EditMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Message message = await LoadAsync(_messages, request.ConversationId, request.MessageId, cancellationToken)
            .ConfigureAwait(false);

        message.Edit(request.EditorId, MessageBody.Create(request.Body), _clock);

        await _events.PublishAsync(message.DomainEvents, cancellationToken).ConfigureAwait(false);
        message.ClearDomainEvents();

        return message;
    }

    /// <summary>
    /// Loads a message scoped to the conversation the caller was authorized for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The conversation id is part of the lookup, not just the route.</b> The membership filter
    /// checked access to <c>conversationId</c>; if the message were fetched by id alone, a caller
    /// could pass a conversation they belong to and a message id from one they do not, and be served
    /// it. Scoping the query is what makes the filter's decision mean something.
    /// </para>
    /// <para>
    /// A missing message is shaped as a refusal so it reads identically to a message in a
    /// conversation the caller cannot see (SC-017).
    /// </para>
    /// </remarks>
    internal static async Task<Message> LoadAsync(
        IMessageRepository messages,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        return await messages
            .FindAsync(conversationId, messageId, sentAt: null, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Message {messageId} was not found in conversation {conversationId}.");
    }
}

/// <summary>
/// Turns a message into a tombstone.
/// </summary>
/// <remarks>
/// The row survives with its body cleared. That is not squeamishness about deletion: ordering,
/// sequence continuity, and the audit trail all depend on the row still being there, and a gap in
/// the sequence would make "everything above seq N" ambiguous on every reconnect afterwards.
/// </remarks>
public sealed class DeleteMessageHandler : IUseCase<DeleteMessage, Message>
{
    private readonly IMessageRepository _messages;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public DeleteMessageHandler(IMessageRepository messages, IEventPublisher events, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _messages = messages;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Message> HandleAsync(
        DeleteMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Message message = await EditMessageHandler
            .LoadAsync(_messages, request.ConversationId, request.MessageId, cancellationToken)
            .ConfigureAwait(false);

        // Idempotent in the domain: a second delete returns without moving DeletedAt, so a retried
        // request does not rewrite the timestamp an auditor reads to establish when the content
        // stopped being available.
        message.Delete(request.ActorId, _clock);

        await _events.PublishAsync(message.DomainEvents, cancellationToken).ConfigureAwait(false);
        message.ClearDomainEvents();

        return message;
    }
}

/// <summary>Rejects an edit whose body could never be stored.</summary>
public sealed class EditMessageValidator : IValidator<EditMessage>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(
        EditMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Body))
        {
            // An empty edit is not a delete. Treating it as one would let a client remove a message
            // past the delete window by editing it to nothing.
            return ValueTask.FromResult(ValidationResult.Fail(
                "body",
                "An edited message still needs a body. Delete the message instead."));
        }

        return ValueTask.FromResult(
            request.Body.Trim().Length > MessageBody.MaximumLength
                ? ValidationResult.Fail(
                    "body",
                    $"A message body is at most {MessageBody.MaximumLength} characters.")
                : ValidationResult.Valid);
    }
}
