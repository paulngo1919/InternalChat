using System.Net;
using System.Text.Json;
using InternalChat.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace InternalChat.Infrastructure.Persistence.Audit;

/// <summary>
/// Writes audit records to PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Uses the ambient <see cref="ChatDbContext"/>, so a record written by the audit pipeline
/// behavior commits in the same transaction as the action it describes. That is what makes
/// "the action happened and was recorded" atomic rather than merely likely.
/// </para>
/// <para>
/// Note what is absent: no update, no delete, no bulk purge. The database grants enforce the
/// same thing independently (see the <c>AuditAppendOnly</c> migration), because a rule that
/// exists only in application code is a rule the application can break.
/// </para>
/// </remarks>
public sealed partial class AuditLog : IAuditLog
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ChatDbContext _context;
    private readonly ILogger<AuditLog> _logger;

    /// <summary>Creates the audit log.</summary>
    public AuditLog(ChatDbContext context, ILogger<AuditLog> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        IPAddress? sourceIp = null;
        if (!string.IsNullOrWhiteSpace(entry.SourceIp) && !IPAddress.TryParse(entry.SourceIp, out sourceIp))
        {
            // An unparseable address must not cost us the whole record. Losing an audit row
            // because a proxy sent a malformed X-Forwarded-For would mean the log is least
            // complete exactly when something unusual is happening.
            UnparseableSourceAddress(_logger, entry.Action);
            sourceIp = null;
        }

        AuditEventRecord record = new()
        {
            OccurredAt = Domain.Common.ClockResolution.Truncate(DateTimeOffset.UtcNow),
            ActorId = entry.ActorId,
            Action = entry.Action,
            SubjectType = entry.SubjectType,
            SubjectId = entry.SubjectId,
            SourceIp = sourceIp,
            Outcome = entry.Outcome switch
            {
                AuditOutcome.Success => "success",
                AuditOutcome.Denied => "denied",
                AuditOutcome.Error => "error",
                _ => "error",
            },
            Detail = entry.Detail is null or { Count: 0 }
                ? "{}"
                : JsonSerializer.Serialize(entry.Detail, SerializerOptions),
        };

        await _context.AuditEvents.AddAsync(record, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Warning,
        Message = "Audit entry for {Action} carried an unparseable source address; recorded without it")]
    private static partial void UnparseableSourceAddress(ILogger logger, string action);
}
