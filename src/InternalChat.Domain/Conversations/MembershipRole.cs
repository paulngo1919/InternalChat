namespace InternalChat.Domain.Conversations;

/// <summary>Role held within one conversation.</summary>
/// <remarks>
/// Scoped to a conversation, never global. Constitution Principle IV forbids role-only
/// authorization precisely because a global "admin" says nothing about <em>which</em> conversation
/// — every decision resolves to a <see cref="Membership"/> row for the specific resource.
/// </remarks>
public enum MembershipRole
{
    /// <summary>Can read and post.</summary>
    Member,

    /// <summary>Can additionally add and remove members, and rename the conversation.</summary>
    Admin,
}
