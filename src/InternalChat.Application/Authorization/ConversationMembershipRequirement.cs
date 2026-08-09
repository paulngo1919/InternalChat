using InternalChat.Domain.Conversations;

namespace InternalChat.Application.Authorization;

/// <summary>
/// "The caller must currently be a member of this conversation," optionally at a minimum role.
/// </summary>
/// <param name="ConversationId">The specific resource. Never a wildcard.</param>
/// <param name="MinimumRole">
/// When set, the caller must hold at least this role. <c>null</c> means any live membership
/// suffices.
/// </param>
/// <remarks>
/// Constitution Principle IV: authorization is resource-scoped. This requirement names one
/// conversation because a role alone says nothing about <em>which</em> conversation — "admin" is
/// a property of a membership, not of a person.
/// </remarks>
public sealed record ConversationMembershipRequirement(
    Guid ConversationId,
    MembershipRole? MinimumRole = null);

/// <summary>Why access was refused. Never sent to the caller.</summary>
/// <remarks>
/// SC-017 requires a refusal to be indistinguishable from a not-found in body and timing, so the
/// caller learns nothing from these. They exist for the audit log and the operator, where knowing
/// whether a request failed for want of a token or for want of a membership is the difference
/// between a misconfigured client and an attempted access.
/// </remarks>
public enum MembershipDenialReason
{
    /// <summary>Access was granted; no denial.</summary>
    None,

    /// <summary>No authenticated caller, or a token with no usable subject.</summary>
    NotAuthenticated,

    /// <summary>The request named no conversation, or an unparseable one.</summary>
    NoResource,

    /// <summary>No live membership row — never a member, or removed.</summary>
    NotAMember,

    /// <summary>A live membership, but below the role the operation requires.</summary>
    InsufficientRole,

    /// <summary>
    /// The decision could not be made. Treated as a refusal.
    /// </summary>
    /// <remarks>
    /// SC-024: a cache-tier outage must cost latency, never an incorrect access decision. An
    /// unavailable dependency is therefore a denial, not a pass-through — the alternative is a
    /// system that opens up precisely when it is least healthy.
    /// </remarks>
    Undetermined,
}

/// <summary>Outcome of one authorization decision.</summary>
/// <param name="IsAllowed">Whether the caller may proceed.</param>
/// <param name="Reason">Why not, when refused.</param>
/// <param name="Membership">The membership that granted access, when allowed.</param>
public sealed record MembershipDecision(
    bool IsAllowed,
    MembershipDenialReason Reason,
    Abstractions.MembershipSnapshot? Membership = null)
{
    /// <summary>Builds an allow decision.</summary>
    public static MembershipDecision Allow(Abstractions.MembershipSnapshot membership) =>
        new(true, MembershipDenialReason.None, membership);

    /// <summary>Builds a deny decision.</summary>
    public static MembershipDecision Deny(MembershipDenialReason reason) => new(false, reason);
}
