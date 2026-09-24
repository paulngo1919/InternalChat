namespace InternalChat.Api.Hubs;

/// <summary>
/// The server-to-client event names from <c>contracts/signalr-hub.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// Constants, because SignalR delivers by name at runtime. A misspelled or renamed event does not
/// fail to compile, does not throw, and does not log — it is simply delivered to nobody, and the
/// symptom is "messages sometimes do not appear" reported weeks later by one team. Naming the
/// events once means the fan-out consumer and T081's contract test cannot disagree.
/// </para>
/// <para>
/// Only the events US2 actually delivers are listed. The contract documents more —
/// <c>AttachmentReady</c>, <c>MeetingStarted</c>, <c>ReadStateUpdated</c> — and they are added by
/// the story that first emits them. A constant for an event nothing sends reads as implemented.
/// </para>
/// </remarks>
public static class ChatHubEvents
{
    /// <summary>A new message in a conversation the client belongs to (FR-009).</summary>
    public const string MessageReceived = nameof(MessageReceived);

    /// <summary>A message's body changed (FR-014).</summary>
    public const string MessageEdited = nameof(MessageEdited);

    /// <summary>
    /// A message became a tombstone (FR-014).
    /// </summary>
    /// <remarks>
    /// Carries <c>{ conversationId, messageId, seq }</c> and deliberately not the message. Sending
    /// the full DTO would put the body of the message being deleted onto the wire, which is the one
    /// payload this event exists to remove.
    /// </remarks>
    public const string MessageDeleted = nameof(MessageDeleted);

    /// <summary>The client was added to a new conversation (US3 scenario 1).</summary>
    public const string ConversationCreated = nameof(ConversationCreated);

    /// <summary>
    /// The client was removed from a conversation (US3 scenario 3).
    /// </summary>
    /// <remarks>
    /// Carries <c>{ conversationId }</c> only, per contracts/signalr-hub.md — a removed member has
    /// no further business knowing anything else about the conversation they just lost access to.
    /// </remarks>
    public const string MembershipRevoked = nameof(MembershipRevoked);

    /// <summary>Read on another device (FR-036). Payload is the same <c>ReadState</c> the HTTP contract uses.</summary>
    public const string ReadStateUpdated = nameof(ReadStateUpdated);

    /// <summary>Who is currently typing in a conversation (FR-016).</summary>
    public const string TypingChanged = nameof(TypingChanged);

    /// <summary>An employee's presence changed (FR-017).</summary>
    public const string PresenceChanged = nameof(PresenceChanged);

    /// <summary>
    /// A meeting has started in a conversation (FR-041).
    /// </summary>
    /// <remarks>
    /// Sent to the conversation's group, which is how every member gets the join prompt without
    /// anyone being invited individually — the meeting belongs to the conversation, so its
    /// membership is exactly the right audience.
    /// </remarks>
    public const string MeetingStarted = nameof(MeetingStarted);

    /// <summary>A meeting has ended (FR-047). Clients dismiss the join prompt.</summary>
    /// <remarks>
    /// Needed as its own event because a meeting ends when the <em>room</em> empties, not when any
    /// particular person leaves — nothing a client can observe locally tells it the prompt is stale.
    /// </remarks>
    public const string MeetingEnded = nameof(MeetingEnded);
}
