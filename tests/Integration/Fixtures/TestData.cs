using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Employees;
using InternalChat.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InternalChat.IntegrationTests.Fixtures;

/// <summary>A clock frozen at a chosen instant.</summary>
/// <remarks>
/// Integration tests still need injectable time. A row written with <c>DateTimeOffset.UtcNow</c>
/// cannot be asserted against without a tolerance, and a tolerance is where flakes live.
/// </remarks>
public sealed class FixedClock : IClock
{
    /// <summary>Creates a clock frozen at <paramref name="utcNow"/>.</summary>
    public FixedClock(DateTimeOffset utcNow) => UtcNow = utcNow;

    /// <summary>Creates a clock frozen at the current instant.</summary>
    public FixedClock()
        : this(InternalChat.Domain.Common.ClockResolution.Truncate(DateTimeOffset.UtcNow))
    {
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; }
}

/// <summary>
/// Builds the rows US1 tests need, using the domain's own constructors.
/// </summary>
/// <remarks>
/// Rows are created through <see cref="Employee.Project"/> and <see cref="Membership.Join"/> rather
/// than by raw INSERT, so a test cannot set up state the application could never produce — a
/// membership with a negative history floor, an employee with a blank subject. A fixture that can
/// build impossible state eventually tests behaviour that cannot happen.
/// </remarks>
public static class TestData
{
    /// <summary>
    /// Subjects match the development realm: <c>employee.external_subject</c> is <c>dev-{username}</c>.
    /// </summary>
    public static string SubjectFor(string username) => $"dev-{username}";

    /// <summary>Inserts an employee whose subject matches a realm user, and returns its id.</summary>
    public static async Task<Guid> SeedEmployeeAsync(
        ChatDbContext context,
        string username,
        bool active = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        FixedClock clock = new();

        Employee employee = Employee.Project(
            Guid.CreateVersion7(),
            SubjectFor(username),
            username,
            $"{username}@internalchat.local",
            clock);

        if (!active)
        {
            employee.Deactivate(clock);
        }

        // Events are drained into the outbox by the pipeline in production. Nothing here runs that
        // pipeline, and an unmapped navigation would fail the save.
        employee.ClearDomainEvents();

        context.Employees.Add(employee);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return employee.Id;
    }

    /// <summary>
    /// Inserts a group conversation with the given id, if one is not already there.
    /// </summary>
    /// <remarks>
    /// Idempotent, because several tests seed two memberships into the same conversation and the
    /// second call must not fail on the primary key.
    /// </remarks>
    public static async Task<Conversation> SeedConversationAsync(
        ChatDbContext context,
        Guid conversationId,
        Guid createdBy,
        HistoryVisibility historyVisibility = HistoryVisibility.Full,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        Conversation? existing = await context.Conversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            return existing;
        }

        FixedClock clock = new();

        Conversation conversation = Conversation.CreateGroup(
            conversationId,
            $"seeded-{conversationId:N}"[..20],
            createdBy,
            historyVisibility,
            clock);

        conversation.ClearDomainEvents();

        context.Conversations.Add(conversation);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return conversation;
    }

    /// <summary>
    /// Inserts a live membership granting <paramref name="employeeId"/> access.
    /// </summary>
    /// <remarks>
    /// Creates the conversation first when it does not exist. T088 added the foreign key from
    /// <c>membership</c> to <c>conversation</c>, so a membership naming a conversation that was
    /// never created is no longer insertable — and was never meaningful, since it is the
    /// authorization record for a conversation there is no way to reach.
    /// </remarks>
    public static async Task SeedMembershipAsync(
        ChatDbContext context,
        Guid conversationId,
        Guid employeeId,
        MembershipRole role = MembershipRole.Member,
        long visibleFromSeq = 0,
        bool removed = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await SeedConversationAsync(context, conversationId, employeeId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        FixedClock clock = new();
        Membership membership = Membership.Join(conversationId, employeeId, role, visibleFromSeq, clock);

        if (removed)
        {
            membership.Remove(clock);
        }

        context.Memberships.Add(membership);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
