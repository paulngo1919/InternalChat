using InternalChat.Application.Abstractions;
using InternalChat.Domain.Conversations;
using Microsoft.Extensions.Logging;

namespace InternalChat.Application.Authorization;

/// <summary>Makes the one authorization decision this platform has.</summary>
public interface IConversationMembershipEvaluator
{
    /// <summary>Decides whether <paramref name="employeeId"/> may act on the named conversation.</summary>
    Task<MembershipDecision> EvaluateAsync(
        Guid employeeId,
        ConversationMembershipRequirement requirement,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Deny-by-default evaluation of conversation membership.
/// </summary>
/// <remarks>
/// <para>
/// T066. Constitution Principle IV: "Authorization MUST be deny-by-default and resource-scoped."
/// Every read of a message, retrieval of an attachment, search result, and meeting join token
/// passes through here, so the shape of this method is the shape of the platform's access model.
/// </para>
/// <para>
/// <b>Every path that is not an explicit allow is a deny.</b> That is why the method has a single
/// <c>Allow</c> return at the end rather than early returns scattered through it — a future branch
/// added in the middle falls through to the refusal, not past it. Getting this backwards is the
/// classic authorization bug: code that denies on the cases it thought of and permits everything
/// it did not.
/// </para>
/// <para>
/// It performs no caching itself. <see cref="IMembershipReader"/>'s implementation owns the
/// 30-second cache and its invalidation (Principle VII), which keeps the policy decision and the
/// storage strategy independently testable — this class is a pure function of what the reader
/// returns.
/// </para>
/// </remarks>
public sealed partial class ConversationMembershipEvaluator : IConversationMembershipEvaluator
{
    private readonly IMembershipReader _memberships;
    private readonly ILogger<ConversationMembershipEvaluator> _logger;

    /// <summary>Creates the evaluator.</summary>
    public ConversationMembershipEvaluator(
        IMembershipReader memberships,
        ILogger<ConversationMembershipEvaluator> logger)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(logger);

        _memberships = memberships;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MembershipDecision> EvaluateAsync(
        Guid employeeId,
        ConversationMembershipRequirement requirement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        // An empty subject means the token carried no usable identity. Treating it as a value and
        // querying for it would look up "the employee with the all-zero id" — which is a real
        // lookup that could, given an unlucky seed row, succeed.
        if (employeeId == Guid.Empty)
        {
            return MembershipDecision.Deny(MembershipDenialReason.NotAuthenticated);
        }

        if (requirement.ConversationId == Guid.Empty)
        {
            return MembershipDecision.Deny(MembershipDenialReason.NoResource);
        }

        MembershipSnapshot? membership;

        try
        {
            membership = await _memberships
                .FindGrantingAsync(requirement.ConversationId, employeeId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed. SC-024 permits a cache or database outage to cost latency; it does not
            // permit it to produce a wrong access decision. Swallowing the error and allowing
            // would open every conversation at the exact moment nobody is watching dashboards.
            EvaluationFailed(_logger, requirement.ConversationId, ex);
            return MembershipDecision.Deny(MembershipDenialReason.Undetermined);
        }

        if (membership is null)
        {
            return MembershipDecision.Deny(MembershipDenialReason.NotAMember);
        }

        // Ordinal comparison works because MembershipRole is ordered least- to most-privileged.
        // A test asserts that ordering, so inserting a role in the middle fails the build rather
        // than silently promoting everyone below it.
        if (requirement.MinimumRole is { } minimum && membership.Role < minimum)
        {
            return MembershipDecision.Deny(MembershipDenialReason.InsufficientRole);
        }

        return MembershipDecision.Allow(membership);
    }

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Error,
        Message = "Membership evaluation for conversation {ConversationId} failed; refusing access")]
    private static partial void EvaluationFailed(ILogger logger, Guid conversationId, Exception exception);
}
