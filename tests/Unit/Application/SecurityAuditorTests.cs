using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;

namespace InternalChat.UnitTests.Application;

/// <summary>
/// The security events that happen outside a use case: sign-in, sign-out, and refusal (FR-006).
/// </summary>
/// <remarks>
/// <para>
/// <b>What makes these worth unit-testing is that a defect here is invisible.</b> Every other bug in
/// this system announces itself — a wrong query returns wrong data, a broken handler throws. An
/// audit record written with the wrong action name, a missing source address, or a reason that
/// leaks what the caller was refused knowledge of produces no error and no failing request. It
/// produces a log that looks fine and answers the wrong question a year later, when SC-021 says an
/// administrator has to answer "who had access on this date, and who changed it" from it alone.
/// </para>
/// <para>
/// <b>The commit is asserted as carefully as the content.</b> <see cref="IAuditLog.RecordAsync"/>
/// only stages the row; committing is the caller's job, and here there is no use case to do it.
/// An auditor that staged without committing would pass every content assertion and write nothing.
/// </para>
/// </remarks>
public sealed class SecurityAuditorTests : UnitTestBase
{
    private readonly RecordingAuditLog _log = new();
    private readonly RecordingUnitOfWork _unitOfWork = new();

    [Fact]
    public async Task A_sign_in_is_recorded_with_the_session_and_the_source_address()
    {
        Guid employeeId = Guid.CreateVersion7();

        await NewAuditor().SignInAsync(employeeId, "session-abc", "203.0.113.7");

        AuditEntry entry = Assert.Single(_log.Entries);

        Assert.Equal("auth.signin", entry.Action);
        Assert.Equal(employeeId, entry.ActorId);
        Assert.Equal("employee", entry.SubjectType);
        Assert.Equal(employeeId, entry.SubjectId);
        Assert.Equal("203.0.113.7", entry.SourceIp);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);

        // The session id is what ties a later sign-out, and every action in between, to this one.
        Assert.Equal("session-abc", entry.Detail!["sessionId"]);
    }

    [Theory]
    [InlineData(SignOutReason.SelfService, "self_service")]
    [InlineData(SignOutReason.BackChannelLogout, "back_channel_logout")]
    [InlineData(SignOutReason.Deactivation, "deactivation")]
    public async Task A_sign_out_records_why_the_session_ended(SignOutReason reason, string expected)
    {
        Guid employeeId = Guid.CreateVersion7();

        await NewAuditor().SignOutAsync(employeeId, "session-abc", reason, sourceIp: null);

        AuditEntry entry = Assert.Single(_log.Entries);

        Assert.Equal("auth.signout", entry.Action);

        // Three reasons that read identically in a session list and mean very different things:
        // someone clicked sign out, Keycloak revoked the session, or the directory deactivated the
        // person. Recorded as stable snake_case strings rather than enum names, so a query written
        // against the log keeps working if the enum is ever renamed.
        Assert.Equal(expected, entry.Detail!["reason"]);
        Assert.Equal("session-abc", entry.Detail["sessionId"]);
    }

    [Fact]
    public async Task An_unrecognised_sign_out_reason_is_recorded_rather_than_dropped()
    {
        // A value added to the enum without being added to the switch. Recording "unknown" keeps
        // the row — a sign-out missing from the log entirely is the worse failure, because it makes
        // the session look like it is still open.
        await NewAuditor().SignOutAsync(
            Guid.CreateVersion7(), "session-abc", (SignOutReason)99, sourceIp: null);

        AuditEntry entry = Assert.Single(_log.Entries);

        Assert.Equal("unknown", entry.Detail!["reason"]);
    }

    [Fact]
    public async Task A_denial_records_the_reason_the_response_deliberately_withholds()
    {
        Guid employeeId = Guid.CreateVersion7();
        Guid conversationId = Guid.CreateVersion7();

        await NewAuditor().AccessDeniedAsync(
            employeeId, "conversation", conversationId, "not_a_member", "203.0.113.7");

        AuditEntry entry = Assert.Single(_log.Entries);

        Assert.Equal("access.denied", entry.Action);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
        Assert.Equal("conversation", entry.SubjectType);
        Assert.Equal(conversationId, entry.SubjectId);

        // The log distinguishes "not a member" from "no such conversation"; the HTTP response
        // deliberately does not (SC-017). That asymmetry is the whole design, and it only works if
        // the reason actually reaches the log.
        Assert.Equal("not_a_member", entry.Detail!["reason"]);
    }

    [Fact]
    public async Task A_denial_with_no_resolved_employee_is_still_recorded()
    {
        // A token whose subject matches no employee. More interesting than an ordinary refusal, not
        // less — dropping it because there is nobody to attribute it to would hide exactly the
        // events worth looking at.
        await NewAuditor().AccessDeniedAsync(
            employeeId: null, "conversation", subjectId: null, "unknown_subject", sourceIp: null);

        AuditEntry entry = Assert.Single(_log.Entries);

        Assert.Null(entry.ActorId);
        Assert.Null(entry.SubjectId);
        Assert.Equal(AuditOutcome.Denied, entry.Outcome);
    }

    [Fact]
    public async Task Every_event_commits_on_its_own()
    {
        SecurityAuditor auditor = NewAuditor();

        await auditor.SignInAsync(Guid.CreateVersion7(), "s1", sourceIp: null);
        await auditor.AccessDeniedAsync(null, "conversation", null, "not_a_member", null);
        await auditor.SignOutAsync(Guid.CreateVersion7(), "s1", SignOutReason.SelfService, null);

        // Three events, three transactions. This is the deliberate mirror of AuditBehavior: a
        // success is recorded inside the caller's transaction so the two are atomic, while these
        // are recorded outside any transaction so a rollback cannot take the evidence of a refusal
        // with it. A denial that vanishes with the request it refused is worse than no log at all,
        // because the log then looks clean.
        Assert.Equal(3, _unitOfWork.Transactions);
        Assert.Equal(3, _log.Entries.Count);
    }

    [Fact]
    public async Task The_record_is_staged_inside_the_transaction_not_beside_it()
    {
        await NewAuditor().SignInAsync(Guid.CreateVersion7(), "s1", sourceIp: null);

        // Ordering, not just counting. Recording outside the transaction would leave the row
        // uncommitted and the audit silently empty.
        Assert.True(_log.WasInsideTransaction);
    }

    [Fact]
    public void The_auditor_refuses_to_be_built_without_its_collaborators()
    {
        Assert.Throws<ArgumentNullException>(() => new SecurityAuditor(null!, _unitOfWork));
        Assert.Throws<ArgumentNullException>(() => new SecurityAuditor(_log, null!));
    }

    private SecurityAuditor NewAuditor()
    {
        // So the log can tell whether it was called from inside the transaction scope.
        _unitOfWork.Log = _log;

        return new SecurityAuditor(_log, _unitOfWork);
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];

        public bool InTransaction { get; set; }

        public bool WasInsideTransaction { get; private set; }

        public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            WasInsideTransaction = InTransaction;
            Entries.Add(entry);

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingUnitOfWork : IUnitOfWork
    {
        public int Transactions { get; private set; }

        public RecordingAuditLog? Log { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public async Task<TResult> ExecuteInTransactionAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            Transactions++;

            if (Log is not null)
            {
                Log.InTransaction = true;
            }

            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                if (Log is not null)
                {
                    Log.InTransaction = false;
                }
            }
        }
    }
}
