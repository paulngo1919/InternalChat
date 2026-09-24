using InternalChat.Domain.Common;

namespace InternalChat.Domain.Conversations;

/// <summary>Whether a conversation is a pair or a named group.</summary>
public enum ConversationKind
{
    /// <summary>Exactly two participants, neither removable (FR-007).</summary>
    Direct = 1,

    /// <summary>A named group with membership that changes over time (FR-008).</summary>
    Group = 2,
}

/// <summary>
/// How much history a newly added member can see (US3 scenario 4).
/// </summary>
/// <remarks>
/// Fixed at creation and displayed to members. A rule that could change later would retroactively
/// grant or withdraw history from people who already joined, which is not a setting — it is a
/// disclosure.
/// </remarks>
public enum HistoryVisibility
{
    /// <summary>Only messages sent after the member joined.</summary>
    FromJoin = 1,

    /// <summary>The whole conversation, including messages sent before joining.</summary>
    Full = 2,
}

/// <summary>
/// A conversation: the unit membership is scoped to, and the sequence allocator for its messages.
/// </summary>
/// <remarks>
/// <para>
/// Two responsibilities that look unrelated and are not. The conversation owns
/// <see cref="LastSeq"/> because ordering is per-conversation (research.md D1), and it owns
/// <see cref="HistoryVisibility"/> because the history floor a new member receives is a function of
/// both the rule and the current sequence. Splitting them would mean a caller computing the floor
/// from two objects, and doing it differently in each of the several places members are added.
/// </para>
/// <para>
/// <b><see cref="DirectKey"/> is the deduplication mechanism.</b> It is the two participant ids
/// sorted and joined, with a UNIQUE index behind it, which is what makes two people clicking
/// "message" on each other at the same moment produce one conversation instead of two. Sorting is
/// the whole trick: a key that depended on argument order would produce two rows and each
/// participant would see half the exchange.
/// </para>
/// </remarks>
public sealed class Conversation : Entity<Guid>
{
    /// <summary>Longest permitted group name, matching <c>contracts/openapi.yaml</c>.</summary>
    public const int MaximumNameLength = 200;

    private Conversation(
        Guid id,
        ConversationKind kind,
        string? name,
        Guid createdBy,
        HistoryVisibility historyVisibility,
        string? directKey,
        DateTimeOffset createdAt)
        : base(id)
    {
        Kind = kind;
        Name = name;
        CreatedBy = createdBy;
        HistoryVisibility = historyVisibility;
        DirectKey = directKey;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    /// <summary>Pair or group.</summary>
    public ConversationKind Kind { get; private set; }

    /// <summary>Group name. Always <c>null</c> for a direct conversation, which is named by who is in it.</summary>
    public string? Name { get; private set; }

    /// <summary>Who created it.</summary>
    public Guid CreatedBy { get; private set; }

    /// <summary>
    /// The history rule. Read-only by design — there is no setter, and a unit test asserts that.
    /// </summary>
    public HistoryVisibility HistoryVisibility { get; }

    /// <summary>
    /// Highest sequence allocated so far. Starts at zero.
    /// </summary>
    /// <remarks>
    /// Zero rather than one, so a member joining an empty conversation gets a floor of 0 and the
    /// first message — seq 1 — passes the <c>seq &gt; visible_from_seq</c> filter. Starting at one
    /// would hide the first message from everyone.
    /// </remarks>
    public long LastSeq { get; private set; }

    /// <summary>
    /// Canonical sorted pair of participant ids, or <c>null</c> for a group.
    /// </summary>
    /// <remarks>
    /// <c>null</c> rather than a sentinel: the unique index is partial
    /// (<c>WHERE kind = 'direct'</c>), and a sentinel would collide across every group on the
    /// platform.
    /// </remarks>
    public string? DirectKey { get; private set; }

    /// <summary>When it was created.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When it last changed.</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Creates a direct conversation between two employees (FR-007).
    /// </summary>
    /// <exception cref="ArgumentException">The two ids are the same.</exception>
    public static Conversation CreateDirect(Guid id, Guid firstEmployeeId, Guid secondEmployeeId, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (firstEmployeeId == secondEmployeeId)
        {
            throw new ArgumentException(
                "A direct conversation needs two distinct employees. A self-conversation satisfies a "
                + "naive count of two and is not what FR-007 describes.",
                nameof(secondEmployeeId));
        }

        return new Conversation(
            id,
            ConversationKind.Direct,
            name: null,
            createdBy: firstEmployeeId,

            // Full, not FromJoin. Both participants were present from the first message by
            // construction, so there is no join point worth hiding anything behind.
            HistoryVisibility.Full,
            BuildDirectKey(firstEmployeeId, secondEmployeeId),
            clock.UtcNow);
    }

    /// <summary>
    /// Creates a named group (FR-008).
    /// </summary>
    /// <exception cref="ArgumentException">The name is missing or too long.</exception>
    public static Conversation CreateGroup(
        Guid id,
        string name,
        Guid createdBy,
        HistoryVisibility historyVisibility,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new Conversation(
            id,
            ConversationKind.Group,
            RequireName(name),
            createdBy,
            historyVisibility,
            directKey: null,
            clock.UtcNow);
    }

    /// <summary>
    /// Builds the canonical direct key for a pair.
    /// </summary>
    /// <remarks>
    /// Exposed so <c>CreateConversation</c> can look up an existing conversation by key before
    /// attempting to create one, and so <c>tools/Seeder</c> builds the identical value. The two
    /// spellings drifting apart would make seeded conversations invisible to deduplication, which
    /// then tries an insert the unique index refuses.
    /// </remarks>
    public static string BuildDirectKey(Guid first, Guid second)
    {
        (Guid low, Guid high) = string.CompareOrdinal(first.ToString(), second.ToString()) <= 0
            ? (first, second)
            : (second, first);

        return $"{low}:{high}";
    }

    /// <summary>
    /// Allocates the next sequence for a message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gapless and monotonic. On the production send path the allocation happens as
    /// <c>UPDATE conversation SET last_seq = last_seq + 1 ... RETURNING</c> inside the send
    /// transaction (research.md D1), because a row lock is what makes it safe under concurrency and
    /// an in-memory increment is not. This method exists so the invariant is expressed and testable
    /// in the domain, and so a loaded-and-tracked conversation stays consistent with the row.
    /// </para>
    /// <para>
    /// The two must agree. If the SQL path and this ever diverge, one of them will hand out a
    /// sequence twice and the unique index on <c>(conversation_id, seq)</c> will start refusing
    /// sends.
    /// </para>
    /// </remarks>
    public long AllocateSequence() => ++LastSeq;

    /// <summary>
    /// Adopts a sequence allocated by the database rather than by this entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The production send path allocates with <c>UPDATE ... RETURNING</c>, which changes the row
    /// without the loaded entity knowing. This tells the entity what happened, so anything else in
    /// the same transaction that reads <see cref="LastSeq"/> — computing a new member's history
    /// floor, for instance — sees the value the row now holds rather than the one it was loaded
    /// with.
    /// </para>
    /// <para>
    /// Only ever moves forward. A lower value could only come from a caller passing a stale
    /// allocation, and silently rewinding the sequence would let the next send reuse a number that
    /// is already taken.
    /// </para>
    /// </remarks>
    public void SynchroniseSequence(long allocatedSeq)
    {
        if (allocatedSeq > LastSeq)
        {
            LastSeq = allocatedSeq;
        }
    }

    /// <summary>
    /// The history floor a member joining now should receive.
    /// </summary>
    /// <remarks>
    /// Computed here because only the conversation knows both its rule and its current sequence.
    /// <c>Membership.Join</c> takes the result rather than deriving it, so there is exactly one
    /// implementation of the rule.
    /// </remarks>
    public long HistoryFloorForNewMember() =>
        HistoryVisibility == HistoryVisibility.Full ? 0 : LastSeq;

    /// <summary>
    /// Throws when membership may not be changed.
    /// </summary>
    /// <remarks>
    /// One gate for adding and removing, not two. "Exactly two members, neither removable" is a
    /// single invariant; splitting it across two methods invites one of them to be forgotten, and
    /// the forgotten one turns a private exchange into something else.
    /// </remarks>
    /// <exception cref="DirectConversationException">This is a direct conversation.</exception>
    public void EnsureMembersMayChange()
    {
        if (Kind == ConversationKind.Direct)
        {
            throw new DirectConversationException(
                Id,
                "Membership of a direct conversation cannot change: it has exactly two "
                + "participants and neither is removable.");
        }
    }

    /// <summary>Renames a group.</summary>
    /// <exception cref="DirectConversationException">This is a direct conversation.</exception>
    public void Rename(string name, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (Kind == ConversationKind.Direct)
        {
            throw new DirectConversationException(
                Id,
                "A direct conversation is named by who is in it. A name would let the two "
                + "participants disagree about what the conversation is called.");
        }

        Name = RequireName(name);
        UpdatedAt = clock.UtcNow;
    }

    /// <summary>Records that something about the conversation changed.</summary>
    public void Touch(IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        UpdatedAt = clock.UtcNow;
    }

    private static string RequireName(string? name)
    {
        string trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ArgumentException(
                "A group conversation requires a name (data-model.md: NOT NULL when kind = 'group').",
                nameof(name));
        }

        if (trimmed.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"A conversation name is at most {MaximumNameLength} characters.",
                nameof(name));
        }

        return trimmed;
    }
}

/// <summary>Thrown when an operation is attempted that a direct conversation does not permit.</summary>
public sealed class DirectConversationException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public DirectConversationException(Guid conversationId, string message)
        : base(message) => ConversationId = conversationId;

    /// <summary>Creates the exception.</summary>
    public DirectConversationException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public DirectConversationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public DirectConversationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The conversation.</summary>
    public Guid ConversationId { get; }
}
