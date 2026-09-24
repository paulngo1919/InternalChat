namespace InternalChat.Api.Contracts;

/// <summary>
/// The wire spellings of <c>conversation_kind</c> (openapi.yaml <c>Conversation.kind</c>).
/// </summary>
/// <remarks>
/// Constants rather than a serialized enum. The domain enum is <c>Direct</c>/<c>Group</c> and the
/// contract says <c>direct</c>/<c>group</c>; letting <c>System.Text.Json</c> derive one from the
/// other would tie the wire format to a C# identifier, so renaming the enum member would silently
/// change the API. <c>tests/Architecture/BoundaryTests.cs</c> forbids the domain enum here anyway.
/// </remarks>
public static class ConversationKinds
{
    /// <summary>Exactly two participants, neither removable (FR-007).</summary>
    public const string Direct = "direct";

    /// <summary>A named group (FR-008).</summary>
    public const string Group = "group";
}

/// <summary>The wire spellings of <c>history_visibility</c>.</summary>
public static class HistoryVisibilities
{
    /// <summary>Only messages sent after the member joined.</summary>
    public const string FromJoin = "from_join";

    /// <summary>The whole conversation.</summary>
    public const string Full = "full";
}

/// <summary>The wire spellings of <c>MembershipRole</c>.</summary>
public static class MembershipRoles
{
    /// <summary>An ordinary participant.</summary>
    public const string Member = "member";

    /// <summary>May change membership and the group name.</summary>
    public const string Admin = "admin";
}

/// <summary>
/// A conversation as the API returns it (<c>Conversation</c> in openapi.yaml).
/// </summary>
/// <param name="Id">Conversation id.</param>
/// <param name="Kind">One of <see cref="ConversationKinds"/>.</param>
/// <param name="Name">Group name, or <c>null</c> for a direct conversation.</param>
/// <param name="HistoryVisibility">
/// One of <see cref="HistoryVisibilities"/>. Returned rather than kept server-side because US3
/// scenario 4 requires members to be shown the rule that applies to them.
/// </param>
/// <param name="LastSeq">
/// Highest sequence allocated. The client's <c>Resync</c> floor and, with the caller's read state,
/// what <paramref name="UnreadCount"/> is computed from.
/// </param>
/// <param name="UnreadCount">
/// Computed as <c>last_seq - GREATEST(last_read_seq, visible_from_seq)</c>, never stored, so it
/// cannot drift from the messages actually present (data-model.md, <c>read_state</c>).
/// </param>
/// <param name="MemberCount">How many active members.</param>
/// <param name="MutedUntil">
/// Always <c>null</c> until US4 (T129, T132) brings mute settings online. Null is the honest answer
/// today — nothing is muted, because nothing can be.
/// </param>
/// <param name="LastMessage">
/// The most recent message, so a conversation list renders a preview without one query per row.
/// </param>
public sealed record ConversationResponse(
    Guid Id,
    string Kind,
    string? Name,
    string HistoryVisibility,
    long LastSeq,
    int UnreadCount,
    int MemberCount,
    DateTimeOffset? MutedUntil,
    MessageResponse? LastMessage);

/// <summary>
/// One page of conversations (<c>ConversationPage</c> in openapi.yaml).
/// </summary>
public sealed record ConversationPageResponse(
    IReadOnlyList<ConversationResponse> Items,
    string? NextCursor);

/// <summary>
/// A message as the API returns it (<c>Message</c> in openapi.yaml).
/// </summary>
/// <param name="Seq">
/// The ordering authority (FR-012). Clients MUST order by this and never by
/// <paramref name="SentAt"/>, which exists to be displayed and to key the partition.
/// </param>
/// <param name="Body">
/// <c>null</c> once deleted. The row survives as a tombstone so ordering and sequence continuity
/// hold; the client renders the absence rather than hiding the row.
/// </param>
/// <param name="Attachments">
/// The images and videos this message carries (FR-021, FR-022). Empty rather than omitted when
/// there are none, so a client iterates it unconditionally.
/// </param>
public sealed record MessageResponse(
    Guid Id,
    Guid ConversationId,
    long Seq,
    Guid AuthorId,
    string ClientMessageKey,
    string? Body,
    DateTimeOffset SentAt,
    DateTimeOffset? EditedAt,
    DateTimeOffset? DeletedAt,
    IReadOnlyList<Guid> Mentions,
    IReadOnlyList<AttachmentResponse> Attachments);

/// <summary>
/// One page of history (<c>MessagePage</c> in openapi.yaml).
/// </summary>
/// <param name="NextCursor">
/// The keyset cursor for the next page — a sequence number, not an offset. An offset would
/// re-read rows and skip or duplicate messages as new ones arrive mid-scroll (FR-013).
/// </param>
/// <param name="HasMore">
/// Whether another page exists. Derived by asking for one row more than the page size and
/// discarding it, so it is a fact rather than a guess from a full page.
/// </param>
public sealed record MessagePageResponse(
    IReadOnlyList<MessageResponse> Items,
    string? NextCursor,
    bool HasMore);

/// <summary>One member of a conversation (<c>Member</c> in openapi.yaml).</summary>
public sealed record MemberResponse(
    EmployeeSummaryResponse Employee,
    string Role,
    DateTimeOffset JoinedAt);

/// <summary>
/// <c>POST /conversations</c> body (<c>CreateConversationRequest</c> in openapi.yaml).
/// </summary>
/// <param name="Kind">One of <see cref="ConversationKinds"/>.</param>
/// <param name="MemberIds">
/// The other participants. The caller is always a member and is never named here — accepting the
/// caller in the list would make "create a conversation I am not in" expressible.
/// </param>
public sealed record CreateConversationRequest(
    string Kind,
    string? Name,
    IReadOnlyList<Guid> MemberIds,
    string? HistoryVisibility);

/// <summary>
/// <c>POST /conversations/{id}/messages</c> body (<c>SendMessageRequest</c> in openapi.yaml).
/// </summary>
/// <param name="ClientMessageKey">
/// A ULID generated by the sender. Required, and the whole of FR-011: the uniqueness of
/// <c>(conversationId, clientMessageKey)</c> is what makes a retried send return the original
/// message instead of creating a second one.
/// </param>
/// <param name="AttachmentIds">
/// Ids from upload tickets already reserved in this conversation, whose bytes have been uploaded.
/// Each is bound to the message on send; one belonging to another conversation is refused, because
/// binding it would move a file across an authorization boundary without any membership check
/// noticing.
/// </param>
public sealed record SendMessageRequest(
    string ClientMessageKey,
    string Body,
    IReadOnlyList<Guid>? AttachmentIds,
    IReadOnlyList<Guid>? Mentions);

/// <summary><c>PATCH /conversations/{id}/messages/{messageId}</c> body.</summary>
public sealed record EditMessageRequest(string Body);

/// <summary><c>POST /conversations/{id}/members</c> body.</summary>
public sealed record AddMemberRequest(Guid EmployeeId, string? Role);
