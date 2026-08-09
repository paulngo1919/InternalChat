using System.Net;

namespace InternalChat.Infrastructure.Persistence.Audit;

/// <summary>
/// One row in the append-only audit log.
/// </summary>
/// <remarks>
/// <para>
/// FR-006 and SC-020. Retained for a year minimum and read by people investigating incidents,
/// so it must be safe to read: no message bodies, no attachment contents, no credentials
/// (FR-056).
/// </para>
/// <para>
/// There is deliberately no way to modify or remove a row — not in this type, not in
/// <see cref="Application.Abstractions.IAuditLog"/>, and not in the database grants. SC-021
/// requires an administrator to answer "who had access on this date, and who changed it" from
/// this log alone, which only holds if the code being investigated cannot rewrite it.
/// </para>
/// </remarks>
public sealed class AuditEventRecord
{
    /// <summary>Monotonic identity, assigned by the database.</summary>
    public long Id { get; set; }

    /// <summary>Server time the action occurred.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Who performed it. <c>null</c> for system actions such as the retention sweep.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>Dotted action name, for example <c>membership.removed</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>What kind of thing was acted on, for example <c>conversation</c>.</summary>
    public string SubjectType { get; set; } = string.Empty;

    /// <summary>Which instance, when applicable.</summary>
    public Guid? SubjectId { get; set; }

    /// <summary>Origin address, when the action arrived over the network.</summary>
    public IPAddress? SourceIp { get; set; }

    /// <summary>Result: <c>success</c>, <c>denied</c>, or <c>error</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Structured context. Never message bodies or credentials (FR-056).</summary>
    public string Detail { get; set; } = "{}";
}
