using InternalChat.Domain.Conversations;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// What an authorization decision needs to know about one membership, and nothing more.
/// </summary>
/// <param name="ConversationId">The conversation access was requested for.</param>
/// <param name="EmployeeId">The employee requesting it.</param>
/// <param name="Role">Role held within this conversation.</param>
/// <param name="VisibleFromSeq">
/// History floor. Returned with the decision so a history query does not have to look the
/// membership up a second time — the authorization check has already read the row.
/// </param>
public sealed record MembershipSnapshot(
    Guid ConversationId,
    Guid EmployeeId,
    MembershipRole Role,
    long VisibleFromSeq);

/// <summary>
/// Reads the membership row that every access decision resolves to.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow: one method, and it answers exactly one question. It is not a repository —
/// there is no "get all memberships" here, because a broad read interface invites an endpoint to
/// fetch memberships and decide for itself, which is how resource-scoped authorization degrades
/// into scattered <c>if</c> statements.
/// </para>
/// <para>
/// The implementation is cache-backed with a 30-second TTL (Principle VII caps authorization data
/// at 60 seconds), falling through to PostgreSQL. Both layers apply the same filter, so a cache
/// miss and a cache hit cannot disagree.
/// </para>
/// </remarks>
public interface IMembershipReader
{
    /// <summary>
    /// Returns the membership when it currently grants access, otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <b>Returns <c>null</c> for every non-granting case</b> — no row, a removed row, or a
    /// deactivated employee — rather than returning the row and letting the caller judge. Callers
    /// that have to interpret a result eventually interpret one of them wrongly, and the wrong
    /// interpretation here is unauthorized access to a conversation.
    /// </remarks>
    Task<MembershipSnapshot?> FindGrantingAsync(
        Guid conversationId,
        Guid employeeId,
        CancellationToken cancellationToken = default);
}
