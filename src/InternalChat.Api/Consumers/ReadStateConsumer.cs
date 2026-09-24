using System.Text.Json;
using InternalChat.Api.Authorization;
using InternalChat.Api.Contracts;
using InternalChat.Api.Hubs;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Employees;
using Microsoft.AspNetCore.SignalR;

namespace InternalChat.Api.Consumers;

/// <summary>
/// T137 — broadcasts a read-position advance to the same employee's other devices (FR-036).
/// </summary>
/// <remarks>
/// <para>
/// The recipient is one employee's own connections, not a conversation's SignalR group — a device
/// that just read a conversation needs to hear about it exactly once, and every *other* device of
/// the same employee needs to hear about it too, but no other employee cares. This is the same
/// shape <see cref="MembershipChangeConsumer"/> resolves through (subject lookup, then the
/// per-process <see cref="HubConnectionRegistry"/>), for the same reason: per-process only, so on
/// the single-replica v1 deployment it reaches every open connection.
/// </para>
/// </remarks>
public sealed partial class ReadStateConsumer : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHubContext<ChatHub> _hub;
    private readonly HubConnectionRegistry _registry;
    private readonly IEmployeeStore _employees;
    private readonly IConversationRepository _conversations;
    private readonly IMembershipRepository _memberships;
    private readonly ILogger<ReadStateConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public ReadStateConsumer(
        IHubContext<ChatHub> hub,
        HubConnectionRegistry registry,
        IEmployeeStore employees,
        IConversationRepository conversations,
        IMembershipRepository memberships,
        ILogger<ReadStateConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(logger);

        _hub = hub;
        _registry = registry;
        _employees = employees;
        _conversations = conversations;
        _memberships = memberships;
        _logger = logger;
    }

    /// <inheritdoc />
    public string QueueName => "readstate.fanout";

    /// <inheritdoc />
    public async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        ReadStateUpdatedPayload payload = Deserialize(envelope);

        IReadOnlyList<Employee> found = await _employees
            .FindManyAsync([payload.EmployeeId], cancellationToken)
            .ConfigureAwait(false);

        if (found.Count == 0)
        {
            EmployeeMissing(_logger, payload.EmployeeId);
            return;
        }

        string subject = found[0].ExternalSubject;

        IReadOnlyList<string> connectionIds =
            [.. _registry.Snapshot().Where(c => string.Equals(c.Subject, subject, StringComparison.Ordinal))
                .Select(c => c.ConnectionId)];

        if (connectionIds.Count == 0)
        {
            // No connection on this replica for this employee right now — nothing to tell.
            return;
        }

        int unreadCount = await ComputeUnreadCountAsync(payload, cancellationToken).ConfigureAwait(false);
        ReadStateResponse response = new(payload.ConversationId, payload.LastReadSeq, unreadCount);

        foreach (string connectionId in connectionIds)
        {
            await _hub.Clients
                .Client(connectionId)
                .SendAsync(ChatHubEvents.ReadStateUpdated, response, cancellationToken)
                .ConfigureAwait(false);
        }

        Delivered(_logger, payload.ConversationId, connectionIds.Count);
    }

    /// <summary>
    /// <c>last_seq - GREATEST(last_read_seq, visible_from_seq)</c> (data-model.md), read fresh
    /// rather than trusted from the event — the event only carries what changed, and the
    /// conversation may have moved since.
    /// </summary>
    private async Task<int> ComputeUnreadCountAsync(ReadStateUpdatedPayload payload, CancellationToken cancellationToken)
    {
        Domain.Conversations.Conversation? conversation = await _conversations
            .FindAsync(payload.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        Domain.Conversations.Membership? membership = await _memberships
            .FindAsync(payload.ConversationId, payload.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null || membership is null || !membership.IsActive)
        {
            return 0;
        }

        long floor = Math.Max(payload.LastReadSeq, membership.VisibleFromSeq);
        return (int)Math.Max(0, conversation.LastSeq - floor);
    }

    private static ReadStateUpdatedPayload Deserialize(MessageEnvelope envelope) =>
        JsonSerializer.Deserialize<ReadStateUpdatedPayload>(envelope.Payload, SerializerOptions)
        ?? throw new InvalidOperationException(
            $"Message {envelope.MessageId} of type '{envelope.Type}' carried an unreadable payload.");

    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Debug,
        Message = "Broadcast read state for conversation {ConversationId} to {ConnectionCount} local connection(s)")]
    private static partial void Delivered(ILogger logger, Guid conversationId, int connectionCount);

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "Skipped read-state broadcast for employee {EmployeeId}: no matching employee exists")]
    private static partial void EmployeeMissing(ILogger logger, Guid employeeId);
}

/// <summary>The queue payload for <c>chat.read_state.updated.v1</c>.</summary>
internal sealed record ReadStateUpdatedPayload(Guid EmployeeId, Guid ConversationId, long LastReadSeq);
