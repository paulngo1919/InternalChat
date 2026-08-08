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
