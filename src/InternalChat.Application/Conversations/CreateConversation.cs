using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;

namespace InternalChat.Application.Conversations;

/// <summary>Creates a direct or group conversation (FR-007, FR-008).</summary>
/// <param name="MemberIds">
/// The other participants. The creator is added automatically and must not appear here — accepting
/// them would make "create a conversation I am not in" expressible.
/// </param>
public sealed record CreateConversation(
    Guid CreatedBy,
    ConversationKind Kind,
    string? Name,
    IReadOnlyList<Guid> MemberIds,
    HistoryVisibility HistoryVisibility) : ITransactionalRequest, IAuditableRequest
{
    /// <inheritdoc />
    /// <remarks>
    /// FR-006 lists conversation creation. The detail carries the kind and the member count, never
    /// the name or the member ids — a group name is content, and an audit log kept for a year is not
    /// where it belongs (FR-056).
    /// </remarks>
    public AuditEntry ToAuditEntry(AuditOutcome outcome) => new(
        "conversation.created",
        CreatedBy,
        "conversation",
        SubjectId: null,
        SourceIp: null,
        outcome,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = Kind.ToString(),
            ["memberCount"] = (MemberIds.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
}

/// <summary>Outcome of a create.</summary>
/// <param name="WasCreated">
/// <c>false</c> when an existing direct conversation was returned instead. The endpoint turns it
/// into 201 or 200, as the contract documents.
/// </param>
public sealed record CreateConversationResult(Conversation Conversation, bool WasCreated);

/// <summary>
/// Creates a conversation, deduplicating direct ones by their canonical pair key.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure being prevented is a split conversation, not a duplicate row.</b> If two
/// colleagues click "message" on each other at the same moment and two rows are created, each ends
/// up in a different one and neither sees the other's messages. Nothing errors, so nothing alerts —
/// they simply conclude the platform loses messages.
/// </para>
/// <para>
/// Two mechanisms, both needed. The lookup by <c>direct_key</c> handles the common case in one
/// SELECT. The unique partial index handles the race the lookup cannot: both callers miss, both
/// insert, and one of them loses. Losing is expected, so the violation is caught and the winner's
/// row is returned — the caller gets the conversation they asked for either way.
/// </para>
/// </remarks>
public sealed class CreateConversationHandler : IUseCase<CreateConversation, CreateConversationResult>
{
    private readonly IConversationRepository _conversations;
    private readonly IMembershipRepository _memberships;
    private readonly IEmployeeStore _employees;
    private readonly IEventPublisher _events;
    private readonly IClock _clock;

    /// <summary>Creates the handler.</summary>
    public CreateConversationHandler(
        IConversationRepository conversations,
        IMembershipRepository memberships,
        IEmployeeStore employees,
        IEventPublisher events,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _conversations = conversations;
        _memberships = memberships;
        _employees = employees;
        _events = events;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<CreateConversationResult> HandleAsync(
        CreateConversation request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<Guid> participants = [.. request.MemberIds.Append(request.CreatedBy).Distinct()];

        await EnsureEveryoneCanJoinAsync(participants, cancellationToken).ConfigureAwait(false);

        return request.Kind == ConversationKind.Direct
            ? await CreateDirectAsync(request, participants, cancellationToken).ConfigureAwait(false)
            : await CreateGroupAsync(request, participants, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CreateConversationResult> CreateDirectAsync(
        CreateConversation request,
        IReadOnlyList<Guid> participants,
        CancellationToken cancellationToken)
    {
        // Validated upstream, so this is a guard against a caller bypassing the validator rather
        // than the primary check.
        if (participants.Count != 2)
        {
            throw new ArgumentException(
                "A direct conversation is between exactly two distinct employees.",
                nameof(request));
        }

        string directKey = Conversation.BuildDirectKey(participants[0], participants[1]);

        Conversation? existing = await _conversations
            .FindByDirectKeyAsync(directKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return new CreateConversationResult(existing, WasCreated: false);
        }

        Conversation conversation = Conversation.CreateDirect(
            Guid.CreateVersion7(),
            participants[0],
            participants[1],
            _clock);

        // Insert and deduplicate in one statement. The SELECT above handles the common case cheaply;
        // this handles the race the SELECT cannot — two people clicking "message" on each other at
        // the same instant. Staging the insert through the change tracker instead let all eight of
        // eight concurrent attempts proceed, and seven of them died on the unique index as HTTP 500s
        // (openapi.yaml documents 200 with the existing conversation for exactly this case).
        Guid winnerId = await _conversations
            .InsertDirectOrGetExistingAsync(conversation, cancellationToken)
            .ConfigureAwait(false);

        if (winnerId != conversation.Id)
        {
            Conversation winner = await _conversations
                .FindAsync(winnerId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Conversation {winnerId} won the direct-key race but could not be read back.");

            // No memberships and no events: the winner created both. Publishing ConversationCreated
            // here as well would tell every participant twice that a conversation they already have
            // was just created.
            return new CreateConversationResult(winner, WasCreated: false);
        }

        await JoinEveryoneAsync(conversation, participants, request.CreatedBy, cancellationToken)
            .ConfigureAwait(false);

        await _events.PublishAsync(conversation.DomainEvents, cancellationToken).ConfigureAwait(false);
        conversation.ClearDomainEvents();

        return new CreateConversationResult(conversation, WasCreated: true);
    }

    private async Task<CreateConversationResult> CreateGroupAsync(
        CreateConversation request,
        IReadOnlyList<Guid> participants,
        CancellationToken cancellationToken)
    {
        Conversation conversation = Conversation.CreateGroup(
            Guid.CreateVersion7(),
            request.Name!,
            request.CreatedBy,
            request.HistoryVisibility,
            _clock);

        await _conversations.AddAsync(conversation, cancellationToken).ConfigureAwait(false);
        await JoinEveryoneAsync(conversation, participants, request.CreatedBy, cancellationToken)
            .ConfigureAwait(false);

        await _events.PublishAsync(conversation.DomainEvents, cancellationToken).ConfigureAwait(false);
        conversation.ClearDomainEvents();

        return new CreateConversationResult(conversation, WasCreated: true);
    }

    /// <summary>
    /// Adds every participant, with the creator as admin.
    /// </summary>
    /// <remarks>
    /// The history floor comes from <see cref="Conversation.HistoryFloorForNewMember"/> so the rule
    /// lives in one place. At creation it is zero for everyone either way — the conversation has no
    /// messages yet — but deriving it rather than hardcoding zero means a change to the rule cannot
    /// leave this path behind.
    /// </remarks>
    private async Task JoinEveryoneAsync(
        Conversation conversation,
        IReadOnlyList<Guid> participants,
        Guid createdBy,
        CancellationToken cancellationToken)
    {
        long floor = conversation.HistoryFloorForNewMember();

        foreach (Guid employeeId in participants)
        {
            MembershipRole role = employeeId == createdBy ? MembershipRole.Admin : MembershipRole.Member;

            await _memberships
                .AddAsync(Membership.Join(conversation.Id, employeeId, role, floor, _clock), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refuses the whole conversation if any named employee is deactivated or unknown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Checked through <see cref="Employee.EnsureCanJoinConversation"/> rather than by reading
    /// <c>Status</c> here, so this path and every other path that adds a member pass the same gate.
    /// </para>
    /// <para>
    /// All-or-nothing. Silently dropping a deactivated member would create a conversation the
    /// creator believes has three people in it and which actually has two, and they would find out
    /// when someone did not answer.
    /// </para>
    /// </remarks>
    private async Task EnsureEveryoneCanJoinAsync(
        IReadOnlyList<Guid> participants,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Employee> found = await _employees
            .FindManyAsync(participants, cancellationToken)
            .ConfigureAwait(false);

        if (found.Count != participants.Count)
        {
            // Shaped as a refusal, not a not-found. A caller must not be able to discover which
            // employee ids exist by trying to start conversations with them (SC-017).
            throw new UnauthorizedAccessException(
                "One or more named employees do not exist.");
        }

        foreach (Employee employee in found)
        {
            employee.EnsureCanJoinConversation();
        }
    }
}

/// <summary>Rejects a create before it can write anything.</summary>
public sealed class CreateConversationValidator : IValidator<CreateConversation>
{
    /// <inheritdoc />
    public ValueTask<ValidationResult> ValidateAsync(
        CreateConversation request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ValidationError> errors = [];

        if (request.MemberIds.Count == 0)
        {
            errors.Add(new ValidationError("memberIds", "Name at least one other participant."));
        }

        if (request.MemberIds.Contains(request.CreatedBy))
        {
            // The creator is added automatically. Naming them is how a "direct conversation with
            // myself" arrives — it satisfies a naive count of two with one person in it.
            errors.Add(new ValidationError(
                "memberIds",
                "The creator is always a member and must not be listed. A conversation with only "
                + "yourself is not a conversation."));
        }

        if (request.Kind == ConversationKind.Direct)
        {
            IReadOnlyList<Guid> others = [.. request.MemberIds.Distinct()];

            if (others.Count != 1)
            {
                errors.Add(new ValidationError(
                    "memberIds",
                    "A direct conversation has exactly one other participant. Use a group for more."));
            }

            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                errors.Add(new ValidationError(
                    "name",
                    "A direct conversation is named by who is in it. A name would let the two "
                    + "participants disagree about what the conversation is called."));
            }
        }
        else if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add(new ValidationError("name", "A group conversation requires a name."));
        }
        else if (request.Name.Trim().Length > Conversation.MaximumNameLength)
        {
            errors.Add(new ValidationError(
                "name",
                $"A conversation name is at most {Conversation.MaximumNameLength} characters."));
        }

        return ValueTask.FromResult(errors.Count == 0 ? ValidationResult.Valid : new ValidationResult(errors));
    }
}
