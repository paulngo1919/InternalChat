using System;
using InternalChat.Domain.Common;

namespace InternalChat.Domain.Exports;

/// <summary>
/// Domain event published when an administrator requests an export of a conversation or employee (FR-055).
/// </summary>
public sealed record ExportRequestedEvent(
    Guid ExportId,
    Guid? ConversationId,
    Guid? EmployeeId,
    DateOnly? From,
    DateOnly? To,
    string? Reason,
    Guid RequestedByEmployeeId) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.CreateVersion7();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
    public string EventType => "export.requested.v1";
}
