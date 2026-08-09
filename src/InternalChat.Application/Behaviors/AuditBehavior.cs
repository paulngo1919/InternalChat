using InternalChat.Application.Abstractions;

namespace InternalChat.Application.Behaviors;

/// <summary>
/// Marks a request as security-relevant, so the pipeline records it to the audit log.
/// </summary>
/// <remarks>
/// Constitution FR-006 lists what MUST be audited: authentication, access denials, membership
/// and permission changes, exports, retention deletions and policy changes, administrative
/// actions, and meetings.
/// </remarks>
public interface IAuditableRequest
{
    /// <summary>Builds the audit record for this request.</summary>
    /// <remarks>
    /// Implementations MUST NOT put message bodies, attachment contents, credentials, or tokens
    /// into the detail dictionary (FR-056). This log is retained for a year and read by people
    /// investigating incidents, so it has to be safe to read.
    /// </remarks>
    AuditEntry ToAuditEntry(AuditOutcome outcome);
}

/// <summary>
/// Writes an audit record for security-relevant use cases.
/// </summary>
/// <remarks>
/// <para>
/// The innermost behavior, which places it <em>inside</em> the transaction. That is the point:
/// on success the state change and its audit record commit atomically, so there is no window in
/// which an action happened but was never recorded.
/// </para>
/// <para>
/// It follows that this behavior does NOT audit failures — a rollback would take the audit row
/// with it. Denials and errors are recorded outside any transaction by the authorization filter
/// and the global exception handler respectively. Splitting it this way is what lets both halves
/// be true: successes are atomic, and failures survive the rollback that caused them.
/// </para>
/// </remarks>
public sealed class AuditBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
{
    private readonly IAuditLog _auditLog;

    /// <summary>Creates the behavior.</summary>
    public AuditBehavior(IAuditLog auditLog)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        _auditLog = auditLog;
    }

    /// <inheritdoc />
    public async Task<TResponse> HandleAsync(
        TRequest request,
        UseCaseContinuation<TResponse> continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);

        TResponse response = await continuation(cancellationToken).ConfigureAwait(false);

        if (request is IAuditableRequest auditable)
        {
            await _auditLog
                .RecordAsync(auditable.ToAuditEntry(AuditOutcome.Success), cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
    }
}

/// <summary>Why a session ended, recorded on the <c>auth.signout</c> audit event.</summary>
public enum SignOutReason
{
    /// <summary>The employee ended this session themselves (FR-005).</summary>
    SelfService,

    /// <summary>Keycloak reported the session ended, via back-channel logout.</summary>
    BackChannelLogout,

    /// <summary>Directory sync deactivated the employee (FR-003).</summary>
    Deactivation,
}

/// <summary>
/// Records the security events that happen outside a use case (T072).
/// </summary>
/// <remarks>
/// <para>
/// FR-006 requires authentication and access denials to be audited, and neither is a use case.
/// A sign-in is the side effect of presenting a token to any endpoint, and a denial is the
/// <em>absence</em> of a use case running — by the time the pipeline in
/// <see cref="AuditBehavior{TRequest,TResponse}"/> would see it, the request has already been
/// refused. Both therefore need a path that does not go through the dispatcher.
/// </para>
/// <para>
/// <b>Each event commits on its own.</b> That is the deliberate mirror of
/// <see cref="AuditBehavior{TRequest,TResponse}"/>: successes are recorded inside the caller's
/// transaction so the action and its record are atomic, while these are recorded outside any
/// transaction so a rollback cannot take the evidence of a refusal with it. A denial that
/// disappears along with the request it refused is worse than no audit log, because the log then
/// looks clean.
/// </para>
/// </remarks>
public interface ISecurityAuditor
{
    /// <summary>
    /// Records that a session began.
    /// </summary>
    /// <remarks>
    /// Called once per session, not once per request — <see cref="Abstractions.ISessionRegistry"/>
    /// reports first sight. An audit log with a row for every HTTP call is one nobody can read,
    /// and SC-021 requires it to be readable a year later.
    /// </remarks>
    Task SignInAsync(Guid employeeId, string sessionId, string? sourceIp, CancellationToken cancellationToken = default);

    /// <summary>Records that a session ended.</summary>
    Task SignOutAsync(
        Guid employeeId,
        string sessionId,
        SignOutReason reason,
        string? sourceIp,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a refusal (FR-006).
    /// </summary>
    /// <param name="employeeId">
    /// The employee refused, when the token resolved to one. <c>null</c> when it did not — which is
    /// itself worth recording, because a token whose subject matches no employee is a more
    /// interesting event than an ordinary refusal.
    /// </param>
    /// <param name="subjectType">What was being reached for, for example <c>conversation</c>.</param>
    /// <param name="subjectId">Which instance, when the request named one.</param>
    /// <param name="reason">
    /// Short machine-readable cause. MUST NOT restate anything the caller was refused knowledge of:
    /// the log distinguishes "not a member" from "no such conversation", the HTTP response
    /// deliberately does not (SC-017).
    /// </param>
    Task AccessDeniedAsync(
        Guid? employeeId,
        string subjectType,
        Guid? subjectId,
        string reason,
        string? sourceIp,
        CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ISecurityAuditor"/>, writing through <see cref="IAuditLog"/>.</summary>
public sealed class SecurityAuditor : ISecurityAuditor
{
    private readonly IAuditLog _auditLog;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>Creates the auditor.</summary>
    public SecurityAuditor(IAuditLog auditLog, IUnitOfWork unitOfWork)
    {
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        _auditLog = auditLog;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public Task SignInAsync(
        Guid employeeId,
        string sessionId,
        string? sourceIp,
        CancellationToken cancellationToken = default) =>
        CommitAsync(
            new AuditEntry(
                "auth.signin",
                employeeId,
                "employee",
                employeeId,
                sourceIp,
                AuditOutcome.Success,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["sessionId"] = sessionId }),
            cancellationToken);

    /// <inheritdoc />
    public Task SignOutAsync(
        Guid employeeId,
        string sessionId,
        SignOutReason reason,
        string? sourceIp,
        CancellationToken cancellationToken = default) =>
        CommitAsync(
            new AuditEntry(
                "auth.signout",
                employeeId,
                "employee",
                employeeId,
                sourceIp,
                AuditOutcome.Success,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sessionId"] = sessionId,
                    ["reason"] = reason switch
                    {
                        SignOutReason.SelfService => "self_service",
                        SignOutReason.BackChannelLogout => "back_channel_logout",
                        SignOutReason.Deactivation => "deactivation",
                        _ => "unknown",
                    },
                }),
            cancellationToken);

    /// <inheritdoc />
    public Task AccessDeniedAsync(
        Guid? employeeId,
        string subjectType,
        Guid? subjectId,
        string reason,
        string? sourceIp,
        CancellationToken cancellationToken = default) =>
        CommitAsync(
            new AuditEntry(
                "access.denied",
                employeeId,
                subjectType,
                subjectId,
                sourceIp,
                AuditOutcome.Denied,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason }),
            cancellationToken);

    /// <summary>
    /// Writes and commits one record on its own.
    /// </summary>
    /// <remarks>
    /// <see cref="IAuditLog.RecordAsync"/> only stages the row — committing is the caller's job,
    /// which for a pipeline audit means the use case's transaction. Here there is no use case, so
    /// the commit has to happen explicitly. Where a transaction is already open (a denial raised
    /// mid-use-case), <c>ExecuteInTransactionAsync</c> joins it rather than nesting.
    /// </remarks>
    private async Task CommitAsync(AuditEntry entry, CancellationToken cancellationToken) =>
        await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                await _auditLog.RecordAsync(entry, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
}
