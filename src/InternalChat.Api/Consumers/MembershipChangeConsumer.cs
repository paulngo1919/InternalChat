using System.Text.Json;
using InternalChat.Api.Authorization;
using InternalChat.Api.Hubs;
using InternalChat.Api.Mapping;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Employees;
using Microsoft.AspNetCore.SignalR;

namespace InternalChat.Api.Consumers;

/// <summary>
/// T116 — moves the affected connection's SignalR group assignment immediately on a membership
/// change (US3 scenario 3, contracts/signalr-hub.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>Per process, like <see cref="HubConnectionRegistry"/> itself.</b> A replica can only move a
/// group assignment for a connection it holds, so at more than one API replica an added or removed
/// employee connected to a different replica keeps their old group membership until they reconnect
/// or <c>Resync</c>. That is an acceptable gap for the v1 single-replica Compose deployment
/// (plan.md), and the failure mode is graceful: a removed member still cannot read anything, because
/// <see cref="Application.Authorization.IConversationMembershipEvaluator"/> re-checks on every
/// request regardless of which SignalR group a socket happens to be in.
/// </para>
/// <para>
/// <b>Looks the employee up by internal id, not by <c>ExternalSubject</c> directly</b>, because
/// <see cref="MembershipChanged"/> carries the internal id — the id every other membership record
/// uses — and <see cref="HubConnectionRegistry"/> indexes connections by the token's <c>sub</c>
/// claim, which is <see cref="Domain.Employees.Employee.ExternalSubject"/>. The one extra read is
/// off the request path.
/// </para>
/// </remarks>
public sealed partial class MembershipChangeConsumer : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHubContext<ChatHub> _hub;
    private readonly HubConnectionRegistry _registry;
    private readonly IEmployeeStore _employees;
    private readonly IConversationReader _conversations;
    private readonly ILogger<MembershipChangeConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public MembershipChangeConsumer(
        IHubContext<ChatHub> hub,
        HubConnectionRegistry registry,
        IEmployeeStore employees,
        IConversationReader conversations,
        ILogger<MembershipChangeConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(employees);
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(logger);

        _hub = hub;
        _registry = registry;
        _employees = employees;
        _conversations = conversations;
        _logger = logger;
    }

    /// <inheritdoc />
    public string QueueName => "membership.fanout";

    /// <inheritdoc />
    public async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        MembershipChangedPayload payload = Deserialize(envelope);

        IReadOnlyList<Employee> found = await _employees
            .FindManyAsync([payload.EmployeeId], cancellationToken)
            .ConfigureAwait(false);

        if (found.Count == 0)
        {
            // The employee row is gone by the time this was processed — plausible under retry, and
            // there is no connection to move for an employee this platform no longer knows.
            EmployeeMissing(_logger, payload.EmployeeId);
            return;
        }

        string subject = found[0].ExternalSubject;
        string group = ChatHub.GroupFor(payload.ConversationId);

        IReadOnlyList<string> connectionIds =
            [.. _registry.Snapshot().Where(c => string.Equals(c.Subject, subject, StringComparison.Ordinal))
                .Select(c => c.ConnectionId)];

        if (connectionIds.Count == 0)
        {
            // Not an error: the employee has no open connection on this replica, most plausibly
            // because they are offline. The group assignment is corrected regardless the next time
            // they connect, in ChatHub.OnConnectedAsync.
            return;
        }

        switch (payload.Change)
        {
            case "added":
                await HandleAddedAsync(payload, connectionIds, group, cancellationToken).ConfigureAwait(false);
                break;

            case "removed":
                await HandleRemovedAsync(payload, connectionIds, group, cancellationToken).ConfigureAwait(false);
                break;

            case "role_changed":
                // Access is unaffected by a role change alone, so there is no group to move and no
                // event a connected client needs right now. Nothing to do until an admin-only UI
                // affordance needs to react to its own privilege changing mid-session.
                break;

            default:
                throw new InvalidOperationException(
                    $"'{payload.Change}' is not a membership change this consumer knows how to "
                    + "handle. The membership.fanout binding matches every chat.membership.* event, "
                    + "so a new change kind needs a case here.");
        }

        Delivered(_logger, payload.Change, payload.ConversationId, connectionIds.Count);
    }

    private async Task HandleAddedAsync(
        MembershipChangedPayload payload,
        IReadOnlyList<string> connectionIds,
        string group,
        CancellationToken cancellationToken)
    {
        ConversationSummary? summary = await _conversations
            .FindForEmployeeAsync(payload.ConversationId, payload.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        if (summary is null)
        {
            // Added and then immediately removed again before this was processed. There is nothing
            // to join the group for or announce.
            return;
        }

        foreach (string connectionId in connectionIds)
        {
            await _hub.Groups.AddToGroupAsync(connectionId, group, cancellationToken).ConfigureAwait(false);

            await _hub.Clients
                .Client(connectionId)
                .SendAsync(ChatHubEvents.ConversationCreated, summary.ToResponse(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleRemovedAsync(
        MembershipChangedPayload payload,
        IReadOnlyList<string> connectionIds,
        string group,
        CancellationToken cancellationToken)
    {
        foreach (string connectionId in connectionIds)
        {
            await _hub.Groups.RemoveFromGroupAsync(connectionId, group, cancellationToken).ConfigureAwait(false);

            await _hub.Clients
                .Client(connectionId)
                .SendAsync(
                    ChatHubEvents.MembershipRevoked,
                    new { conversationId = payload.ConversationId },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static MembershipChangedPayload Deserialize(MessageEnvelope envelope) =>
        JsonSerializer.Deserialize<MembershipChangedPayload>(envelope.Payload, SerializerOptions)
        ?? throw new InvalidOperationException(
            $"Message {envelope.MessageId} of type '{envelope.Type}' carried an unreadable payload. "
            + "A malformed payload is not retryable — it will be malformed on every attempt — so it "
            + "belongs in the DLQ for a person to look at.");

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Debug,
        Message = "Applied membership change {Change} for conversation {ConversationId} to "
            + "{ConnectionCount} local connection(s)")]
    private static partial void Delivered(
        ILogger logger,
        string change,
        Guid conversationId,
        int connectionCount);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Information,
        Message = "Skipped membership change for employee {EmployeeId}: no matching employee exists")]
    private static partial void EmployeeMissing(ILogger logger, Guid employeeId);
}

/// <summary>The queue payload for <c>chat.membership.changed.v1</c>.</summary>
internal sealed record MembershipChangedPayload(
    Guid ConversationId,
    Guid EmployeeId,
    string Change,
    Guid ActorId,
    long VisibleFromSeq);
