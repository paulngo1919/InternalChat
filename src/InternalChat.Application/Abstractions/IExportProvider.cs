using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace InternalChat.Application.Abstractions;

/// <summary>
/// Provides data for export jobs.
/// </summary>
public interface IExportProvider
{
    /// <summary>Exports all messages for a given conversation as a JSON stream.</summary>
    Task<Stream> ExportConversationAsync(Guid conversationId, DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken = default);

    /// <summary>Exports all messages sent by a given employee as a JSON stream.</summary>
    Task<Stream> ExportEmployeeAsync(Guid employeeId, DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken = default);
}
