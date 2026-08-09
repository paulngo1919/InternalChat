using System.Text.Json;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Employees;
using InternalChat.Infrastructure.Persistence;
using InternalChat.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using InternalChat.Worker.Consumers;

namespace InternalChat.IntegrationTests.Directory;

/// <summary>
/// T065 — directory sync projects employee lifecycle and ends access on deactivation.
/// </summary>
/// <remarks>
/// <para>
/// FR-001 makes the corporate directory authoritative for who is an employee, and FR-003 gives five
/// minutes from a departure being reported to access ending everywhere. This consumer is the first
/// link in that chain: everything downstream — the HTTP access gate, the hub sweep — reads what it
/// writes.
/// </para>
/// <para>
/// Run against real PostgreSQL and real Redis. The interesting assertions are about what is
/// actually in the row and the keyspace afterwards, and both would be assumptions rather than
/// observations against a double.
/// </para>
/// </remarks>
public sealed class DirectorySyncTests : IntegrationTestBase, IAsyncLifetime
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ApiFactory _api;

    public DirectorySyncTests(StackFixture stack)
        : base(stack) => _api = new ApiFactory(stack);

    public override async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task An_upsert_creates_the_employee_row()
    {
        const string Subject = "dev-newcomer";

        await HandleAsync(Subject, "upserted", displayName: "Nguyễn Văn Mới", email: "moi@internalchat.local");

        await using ChatDbContext context = CreateDbContext();
        Employee employee = await context.Employees.SingleAsync(e => e.ExternalSubject == Subject);

        Assert.Equal("Nguyễn Văn Mới", employee.DisplayName);
        Assert.Equal("moi@internalchat.local", employee.Email);
        Assert.True(employee.IsActive);
    }

    /// <summary>
    /// A repeated upsert must update, not duplicate.
    /// </summary>
    /// <remarks>
    /// The unique index on <c>external_subject</c> would turn a second insert into an exception,
    /// which the retry policy would then replay until it dead-lettered — so directory sync would
    /// stop for everyone the first time an employee changed their name.
    /// </remarks>
    [Fact]
    public async Task A_second_upsert_updates_rather_than_duplicating()
    {
        const string Subject = "dev-renamed";

        await HandleAsync(Subject, "upserted", "Trần Thị Cũ", "cu@internalchat.local");
        await HandleAsync(Subject, "upserted", "Trần Thị Mới", "moi2@internalchat.local");

        await using ChatDbContext context = CreateDbContext();

        Employee employee = await context.Employees.SingleAsync(e => e.ExternalSubject == Subject);

        Assert.Equal("Trần Thị Mới", employee.DisplayName);
        Assert.Equal(1, await context.Employees.CountAsync(e => e.ExternalSubject == Subject));
    }

    /// <summary>
    /// The assertion FR-003 rests on: deactivation both moves the row and writes the revocation set.
    /// </summary>
    [Fact]
    public async Task Deactivation_marks_the_row_and_revokes_the_subject()
    {
        const string Subject = "dev-departing";

        await HandleAsync(Subject, "upserted", "Lê Văn Đi", "di@internalchat.local");

        IRevocationStore revocations = _api.Services.GetRequiredService<IRevocationStore>();
        Assert.False(await revocations.IsRevokedAsync(Subject, null));

        await HandleAsync(Subject, "deactivated");

        await using (ChatDbContext context = CreateDbContext())
        {
            Employee employee = await context.Employees.SingleAsync(e => e.ExternalSubject == Subject);

            Assert.False(employee.IsActive);
            Assert.NotNull(employee.DeactivatedAt);
        }

        Assert.True(
            await revocations.IsRevokedAsync(Subject, null),
            """
            The employee row was marked deactivated but the revocation set was not written.

            The row alone is not enough inside the five-minute budget: an already-open hub
            connection is closed by the sweep, which reads the revocation set, and an HTTP request
            is refused by the access gate, which checks it before the row.
            """);
    }

    /// <summary>
    /// A redelivered deactivation must not move <c>deactivated_at</c>.
    /// </summary>
    /// <remarks>
    /// That timestamp is what an auditor reads to establish when access actually ended (SC-021).
    /// Delivery is at-least-once, so "the same deactivation twice" is a normal Tuesday, not an
    /// edge case — and the second one silently rewriting the answer would be undetectable.
    /// </remarks>
    [Fact]
    public async Task A_repeated_deactivation_does_not_move_the_timestamp()
    {
        const string Subject = "dev-twice";

        await HandleAsync(Subject, "upserted", "Phạm Hai Lần", "hailan@internalchat.local");
        await HandleAsync(Subject, "deactivated");

        DateTimeOffset? first;
        await using (ChatDbContext context = CreateDbContext())
        {
            first = (await context.Employees.SingleAsync(e => e.ExternalSubject == Subject)).DeactivatedAt;
        }

        await HandleAsync(Subject, "deactivated");

        await using (ChatDbContext context = CreateDbContext())
        {
            DateTimeOffset? second =
                (await context.Employees.SingleAsync(e => e.ExternalSubject == Subject)).DeactivatedAt;

            Assert.Equal(first, second);
        }
    }

    /// <summary>
    /// Rehiring must clear the revocation entry, not wait it out.
    /// </summary>
    /// <remarks>
    /// Otherwise a reactivated employee signs in successfully and then gets 401s until the entry
    /// expires — which looks like a broken platform rather than like a policy.
    /// </remarks>
    [Fact]
    public async Task Reactivation_restores_the_row_and_clears_the_revocation()
    {
        const string Subject = "dev-rehired";

        await HandleAsync(Subject, "upserted", "Hoàng Trở Lại", "trolai@internalchat.local");
        await HandleAsync(Subject, "deactivated");
        await HandleAsync(Subject, "reactivated");

        await using (ChatDbContext context = CreateDbContext())
        {
            Employee employee = await context.Employees.SingleAsync(e => e.ExternalSubject == Subject);

            Assert.True(employee.IsActive);
            Assert.Null(employee.DeactivatedAt);
        }

        IRevocationStore revocations = _api.Services.GetRequiredService<IRevocationStore>();
        Assert.False(await revocations.IsRevokedAsync(Subject, null));
    }

    /// <summary>
    /// An attribute update must never restore access.
    /// </summary>
    /// <remarks>
    /// The scenario is ordinary: an employee leaves, and a stale or unrelated attribute sync for
    /// the same person arrives afterwards. If an upsert could move <c>Status</c>, that message
    /// would quietly reinstate someone who has left — and the domain forbids it precisely so this
    /// cannot happen by ordering accident.
    /// </remarks>
    [Fact]
    public async Task An_upsert_after_a_deactivation_does_not_restore_access()
    {
        const string Subject = "dev-stale-update";

        await HandleAsync(Subject, "upserted", "Đỗ Văn Cũ", "docu@internalchat.local");
        await HandleAsync(Subject, "deactivated");
        await HandleAsync(Subject, "upserted", "Đỗ Văn Cũ Hơn", "docu2@internalchat.local");

        await using ChatDbContext context = CreateDbContext();
        Employee employee = await context.Employees.SingleAsync(e => e.ExternalSubject == Subject);

        Assert.Equal("Đỗ Văn Cũ Hơn", employee.DisplayName);
        Assert.False(employee.IsActive);
    }

    /// <summary>
    /// A deactivation for an unknown subject still revokes.
    /// </summary>
    /// <remarks>
    /// There is no row to mark, but a token for that subject may well be in circulation — the
    /// employee could have signed in before the projection caught up. Refusing to act because the
    /// projection is behind would leave exactly the token that most needs revoking.
    /// </remarks>
    [Fact]
    public async Task A_deactivation_for_an_unknown_subject_still_revokes()
    {
        const string Subject = "dev-never-seen";

        await HandleAsync(Subject, "deactivated");

        IRevocationStore revocations = _api.Services.GetRequiredService<IRevocationStore>();
        Assert.True(await revocations.IsRevokedAsync(Subject, null));

        await using ChatDbContext context = CreateDbContext();
        Assert.False(await context.Employees.AnyAsync(e => e.ExternalSubject == Subject));
    }

    /// <summary>
    /// An unrecognised change is refused rather than ignored.
    /// </summary>
    /// <remarks>
    /// contracts/messaging.md requires consumers to tolerate unknown <em>fields</em>. The value of
    /// the field that decides what to do is different: silently treating a future "suspended" as a
    /// no-op would leave access in place and report success.
    /// </remarks>
    [Fact]
    public async Task An_unrecognised_change_fails_loudly()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HandleAsync("dev-unknown-change", "suspended"));
    }

    /// <summary>Runs one directory event through the real consumer and use-case pipeline.</summary>
    private async Task HandleAsync(
        string subject,
        string change,
        string? displayName = null,
        string? email = null)
    {
        using IServiceScope scope = _api.Services.CreateScope();

        DirectorySyncConsumer consumer = ActivatorUtilities.CreateInstance<DirectorySyncConsumer>(
            scope.ServiceProvider);

        string payload = JsonSerializer.Serialize(
            new
            {
                externalSubject = subject,
                change,
                displayName,
                email,
                avatarUrl = (string?)null,

                // An extra field the consumer has never heard of. contracts/messaging.md requires
                // consumers to ignore these rather than fail, because that is what makes an
                // additive contract change deployable without stopping the world.
                unknownFutureField = "ignored",
            },
            SerializerOptions);

        await consumer.HandleAsync(
            new MessageEnvelope(
                Guid.CreateVersion7(),
                "chat.directory.employee.changed.v1",
                payload,
                TraceParent: null,
                Attempt: 1));

        // The consumer host commits; here the scope's unit of work stands in for it.
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }
}
