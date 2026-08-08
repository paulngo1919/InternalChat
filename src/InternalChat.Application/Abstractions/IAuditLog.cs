namespace InternalChat.Application.Abstractions;

/// <summary>Outcome of an audited action.</summary>
public enum AuditOutcome
{
    /// <summary>The action completed.</summary>
    Success,

    /// <summary>The action was refused by an authorization decision.</summary>
    Denied,

    /// <summary>The action failed for a non-authorization reason.</summary>
    Error,
}

/// <summary>One append-only audit record.</summary>
/// <param name="Action">Dotted action name, for example <c>membership.removed</c>.</param>
/// <param name="ActorId">Who performed it. <c>null</c> for system actions such as the retention sweep.</param>
/// <param name="SubjectType">What kind of thing was acted on, for example <c>conversation</c>.</param>
/// <param name="SubjectId">Which instance, when applicable.</param>
/// <param name="SourceIp">Origin address, when the action came over the network.</param>
/// <param name="Outcome">Result of the action.</param>
/// <param name="Detail">
/// Structured context. MUST NOT contain message bodies, attachment contents, credentials, or
/// tokens (FR-056) — the audit log is retained a year and read by people investigating
/// incidents, so it must be safe to read.
/// </param>
public sealed record AuditEntry(
    string Action,
    Guid? ActorId,
    string SubjectType,
    Guid? SubjectId,
    string? SourceIp,
    AuditOutcome Outcome,
    IReadOnlyDictionary<string, string>? Detail = null);

/// <summary>
/// Records security-relevant actions to the append-only audit log.
/// </summary>
/// <remarks>
/// <para>
/// FR-006 and SC-020: authentication, access denials, membership and permission changes,
/// exports, retention deletions, retention-policy changes, administrative actions, and meetings
/// are all recorded with actor, subject, timestamp, source address, and outcome.
/// </para>
/// <para>
/// There is no update or delete method, and the database grants the application INSERT only.
/// SC-021 requires an administrator to answer "who had access on this date, and who changed it"
/// from this log alone — which only holds if the log cannot be rewritten by the code being
/// investigated.
/// </para>
/// </remarks>
public interface IAuditLog
{
    /// <summary>Appends one record. Never throws away a record to keep a request succeeding.</summary>
    Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
