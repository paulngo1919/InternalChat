using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;

namespace InternalChat.Application.Conversations;

/// <summary>Adds a member to a group conversation (FR-008, US3 scenario 1).</summary>
/// <remarks>
/// Authorization is the endpoint's job: <c>.RequireConversationMembership(MembershipRole.Admin)</c>
/// establishes the caller already holds an admin membership before this handler runs, matching
/// spec.md US1 scenario 5 ("an administrator ... changes a member's role or removes them"). Nothing
/// here re-checks that — a handler re-deriving a decision the filter already made is a second place
/// for the two to disagree.
/// </remarks>
public sealed record AddMember(
    Guid ConversationId,
    Guid ActorId,
    Guid EmployeeId,
    MembershipRole Role) : ITransactionalRequest, IAuditableRequest
{
    /// <inheritdoc />
    public AuditEntry ToAuditEntry(AuditOutcome outcome) => new(
        "membership.added",
        ActorId,
        "conversation",
        ConversationId,
        SourceIp: null,
        outcome,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["employeeId"] = EmployeeId.ToString(),
            ["role"] = Role.ToString(),
        });
}

/// <summary>
/// Joins an employee to a group, or restores a removed membership.
/// </summary>
/// <remarks>
/// <para>
/// <b>A removed membership is reused, not replaced.</b> <see cref="IMembershipRepository.FindAsync"/>
/// returns removed rows for exactly this reason: <see cref="Membership.Rejoin"/> takes the higher of
/// the old and new history floor, so someone re-added after being removed never regains the history
/// they lost. A fresh row would have no old floor to compare against.
/// </para>
/// <para>
/// Cache invalidation and the domain event both happen here, inside the same transaction the
/// membership row commits in — Constitution Principle VII requires the former; the latter reaches
/// the outbox atomically with the state change for the same reason every other event does.
/// </para>
/// </remarks>
public sealed class AddMemberHandler : IUseCase<AddMember, Membership>
{
    private readonly IConversationRepository _conversations;
    private readonly IMembershipRepository _memberships;
    private readonly IEmployeeStore _employees;
    private readonly IMembershipCacheInvalidator _cache;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public AddMemberHandler(
        IConversationRepository conversations,
        IMembershipRepository memberships,
        IEmployeeStore employees,
        IMembershipCacheInvalidator cache,
        IEventPublisher events,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _conversations = conversations;
        _memberships = memberships;
        _employees = employees;
        _cache = cache;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<Membership> HandleAsync(AddMember request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The membership filter has already confirmed the conversation is reachable by the caller,
        // so a missing row here means it was deleted between the two — refused, not created.
        Conversation conversation = await _conversations
            .FindAsync(request.ConversationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                $"Conversation {request.ConversationId} does not exist.");

        conversation.EnsureMembersMayChange();

        IReadOnlyList<Employee> found = await _employees
            .FindManyAsync([request.EmployeeId], cancellationToken)
            .ConfigureAwait(false);

        // Shaped as a refusal rather than a not-found, for the same enumeration reason
        // CreateConversation refuses an unknown participant (SC-017).
        Employee employee = found.Count == 1
            ? found[0]
            : throw new UnauthorizedAccessException($"Employee {request.EmployeeId} does not exist.");

        employee.EnsureCanJoinConversation();

        long floor = conversation.HistoryFloorForNewMember();

        Membership? membership = await _memberships
            .FindAsync(request.ConversationId, request.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        if (membership is null)
        {
            membership = Membership.Join(request.ConversationId, request.EmployeeId, request.Role, floor, _clock);
            await _memberships.AddAsync(membership, cancellationToken).ConfigureAwait(false);
        }
        else if (membership.IsActive)
        {
            throw new MemberAlreadyActiveException(request.ConversationId, request.EmployeeId);
        }
        else
        {
            membership.Rejoin(floor, _clock);
        }

        await _cache.InvalidateConversationAsync(request.ConversationId, cancellationToken).ConfigureAwait(false);

        await _events.PublishAsync(
            MembershipChanged.Create(
                request.ConversationId,
                request.EmployeeId,
                MembershipChangeKind.Added,
                request.ActorId,
                membership.VisibleFromSeq,
                _clock),
            cancellationToken).ConfigureAwait(false);

        return membership;
    }
}
