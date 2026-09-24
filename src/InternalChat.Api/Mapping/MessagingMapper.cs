using InternalChat.Api.Contracts;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Messages;

namespace InternalChat.Api.Mapping;

/// <summary>
/// Turns domain objects into the wire contract.
/// </summary>
/// <remarks>
/// <para>
/// A single place, because <c>tests/Architecture/BoundaryTests.cs</c> forbids a Domain type from
/// appearing in an Api contract and the mapping is where that rule is actually honoured. Scattering
/// it across endpoints would mean each one deciding independently what to expose — and the first
/// one to serialize a <see cref="Conversation"/> directly would publish <c>DirectKey</c>, which
/// contains both participants' internal ids.
/// </para>
/// <para>
/// The enum spellings come from <see cref="ConversationKinds"/> and
/// <see cref="HistoryVisibilities"/> rather than from <c>ToString()</c>. A default enum
/// serialization would emit <c>FromJoin</c>, and a client comparing that against the documented
/// <c>from_join</c> falls through to its "show the whole history" branch — telling a new member they
/// can read history US3 scenario 4 says they cannot.
/// </para>
/// </remarks>
public static class MessagingMapper
{
    /// <summary>Maps a message.</summary>
    /// <remarks>
    /// <c>Body</c> is <c>null</c> for a tombstone, which is exactly what the contract declares. The
    /// client renders the absence rather than hiding the row, so ordering stays intact.
    /// </remarks>
    /// <param name="attachments">
    /// What this message carries. Defaults to empty, so the many call sites serving text-only
    /// messages stay unchanged and a caller that forgets it renders no attachment rather than a
    /// wrong one.
    /// </param>
    public static MessageResponse ToResponse(
        this Message message,
        IEnumerable<Domain.Attachments.Attachment>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new MessageResponse(
            message.Id,
            message.ConversationId,
            message.Seq,
            message.AuthorId,
            message.ClientMessageKey.Value,
            message.Body?.Value,
            message.SentAt,
            message.EditedAt,
            message.DeletedAt,
            message.Mentions,

            // Empty rather than omitted when there are none, so a client iterates unconditionally.
            Attachments: attachments?.ToResponses() ?? []);
    }

    /// <summary>Maps a conversation summary, with the last message inlined as a preview.</summary>
    public static ConversationResponse ToResponse(this ConversationSummary summary, Message? lastMessage = null)
    {
        ArgumentNullException.ThrowIfNull(summary);

        return new ConversationResponse(
            summary.Conversation.Id,
            ToWire(summary.Conversation.Kind),
            summary.Conversation.Name,
            ToWire(summary.Conversation.HistoryVisibility),
            summary.Conversation.LastSeq,
            summary.UnreadCount,
            summary.MemberCount,

            summary.MutedUntil,
            lastMessage?.ToResponse());
    }

    /// <summary>The documented spelling of a conversation kind.</summary>
    public static string ToWire(ConversationKind kind) => kind switch
    {
        ConversationKind.Direct => ConversationKinds.Direct,
        ConversationKind.Group => ConversationKinds.Group,

        // Not a silent fallback. A new kind added to the domain without a wire spelling would
        // otherwise serialize as something no client has a branch for, and the symptom would be a
        // conversation that renders as neither a chat nor a group.
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind),
            kind,
            "No wire spelling is defined for this conversation kind. Add it to ConversationKinds "
            + "and to openapi.yaml's Conversation.kind enum together."),
    };

    /// <summary>The documented spelling of a history-visibility rule.</summary>
    public static string ToWire(HistoryVisibility visibility) => visibility switch
    {
        HistoryVisibility.FromJoin => HistoryVisibilities.FromJoin,
        HistoryVisibility.Full => HistoryVisibilities.Full,
        _ => throw new ArgumentOutOfRangeException(
            nameof(visibility),
            visibility,
            "No wire spelling is defined for this history-visibility rule."),
    };

    /// <summary>The documented spelling of a membership role.</summary>
    public static string ToWire(MembershipRole role) => role switch
    {
        MembershipRole.Member => MembershipRoles.Member,
        MembershipRole.Admin => MembershipRoles.Admin,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No wire spelling for this role."),
    };

    /// <summary>Parses a client-supplied conversation kind.</summary>
    public static bool TryParseKind(string? value, out ConversationKind kind)
    {
        switch (value)
        {
            case ConversationKinds.Direct:
                kind = ConversationKind.Direct;
                return true;
            case ConversationKinds.Group:
                kind = ConversationKind.Group;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>
    /// Parses a client-supplied membership role, defaulting to <see cref="MembershipRole.Member"/>.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means "not specified" and takes the contract's implicit default. An unrecognised
    /// non-null value is rejected rather than silently treated as <c>member</c> — the two spellings
    /// this platform accepts are exhaustive, and a caller passing anything else has a bug worth
    /// surfacing rather than a preference to guess at.
    /// </remarks>
    public static bool TryParseRole(string? value, out MembershipRole role)
    {
        switch (value)
        {
            case null:
            case MembershipRoles.Member:
                role = MembershipRole.Member;
                return true;
            case MembershipRoles.Admin:
                role = MembershipRole.Admin;
                return true;
            default:
                role = default;
                return false;
        }
    }

    /// <summary>
    /// Parses a client-supplied history-visibility rule, defaulting as the contract documents.
    /// </summary>
    /// <remarks>
    /// <c>null</c> means "not specified" and takes the contract's default of <c>from_join</c>. An
    /// unrecognised <em>non-null</em> value is rejected rather than defaulted: silently treating a
    /// typo as <c>from_join</c> would be defensible, but silently treating one as <c>full</c> would
    /// disclose history, and a parser that cannot tell the two apart should not guess either way.
    /// </remarks>
    public static bool TryParseHistoryVisibility(string? value, out HistoryVisibility visibility)
    {
        switch (value)
        {
            case null:
            case HistoryVisibilities.FromJoin:
                visibility = HistoryVisibility.FromJoin;
                return true;
            case HistoryVisibilities.Full:
                visibility = HistoryVisibility.Full;
                return true;
            default:
                visibility = default;
                return false;
        }
    }
}
