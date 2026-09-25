using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Exports;
using Microsoft.Extensions.Logging;

namespace InternalChat.Worker.Jobs;

/// <summary>
/// T209 — processes export requests, writes JSON to MinIO, and records the outcome (FR-055).
/// </summary>
public sealed partial class ExportJob : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerialOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly IExportProvider _exportProvider;
    private readonly IObjectStore _objectStore;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<ExportJob> _logger;

    public string QueueName => "export.requested.v1";

    public ExportJob(
        IExportProvider exportProvider,
        IObjectStore objectStore,
        IAuditLog auditLog,
        ILogger<ExportJob> logger)
    {
        ArgumentNullException.ThrowIfNull(exportProvider);
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(logger);

        _exportProvider = exportProvider;
        _objectStore = objectStore;
        _auditLog = auditLog;
        _logger = logger;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not deserialize ExportRequestedEvent from envelope payload")]
    private partial void LogDeserializeError();

    [LoggerMessage(Level = LogLevel.Information, Message = "Export {ExportId} completed successfully.")]
    private partial void LogExportSuccess(Guid exportId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Export {ExportId} failed.")]
    private partial void LogExportFailed(Exception ex, Guid exportId);

    public async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var evt = JsonSerializer.Deserialize<ExportRequestedEvent>(
            envelope.Payload,
            SerialOptions);
            
        if (evt == null)
        {
            LogDeserializeError();
            return;
        }

        string objectName = $"exports/{evt.ExportId}.json";

        try
        {
            using var stream = evt.ConversationId.HasValue
                ? await _exportProvider.ExportConversationAsync(evt.ConversationId.Value, evt.From, evt.To, cancellationToken).ConfigureAwait(false)
                : await _exportProvider.ExportEmployeeAsync(evt.EmployeeId!.Value, evt.From, evt.To, cancellationToken).ConfigureAwait(false);

            await _objectStore.PutCleanObjectAsync(
                objectName,
                stream,
                "application/json",
                cancellationToken).ConfigureAwait(false);

            await _auditLog.RecordAsync(new AuditEntry(
                Action: "export.completed",
                ActorId: evt.RequestedByEmployeeId,
                SubjectType: "export",
                SubjectId: evt.ExportId,
                SourceIp: null, // Worker context
                Outcome: AuditOutcome.Success,
                Detail: new System.Collections.Generic.Dictionary<string, string>
                {
                    ["objectName"] = objectName
                }
            ), cancellationToken).ConfigureAwait(false);
            
            LogExportSuccess(evt.ExportId);
        }
        catch (Exception ex)
        {
            LogExportFailed(ex, evt.ExportId);

            await _auditLog.RecordAsync(new AuditEntry(
                Action: "export.failed",
                ActorId: evt.RequestedByEmployeeId,
                SubjectType: "export",
                SubjectId: evt.ExportId,
                SourceIp: null, // Worker context
                Outcome: AuditOutcome.Error,
                Detail: new System.Collections.Generic.Dictionary<string, string>
                {
                    ["error"] = ex.Message
                }
            ), cancellationToken).ConfigureAwait(false);

            throw; // Rethrow to let ConsumerHost handle retry/dead-lettering
        }
    }
}
