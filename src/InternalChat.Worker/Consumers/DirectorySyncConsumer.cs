using System.Text.Json;
using InternalChat.Application.Abstractions;
using InternalChat.Application.Behaviors;
using InternalChat.Application.Directory;

namespace InternalChat.Worker.Consumers;

/// <summary>
/// T065 — projects corporate directory changes onto the <c>employee</c> table.
/// </summary>
/// <remarks>
/// <para>
/// FR-001: this platform never owns employee lifecycle. Employees are created, renamed, and
/// deactivated in the corporate directory, and this consumer is the only writer of the projection —
/// which is why <see cref="IEmployeeDirectory"/> has no write methods at all. A second writer would
/// make "who is an employee" a question with two answers.
/// </para>
/// <para>
/// <b>Deactivation is the whole reason the deadline exists.</b> FR-003 gives five minutes from the
/// directory reporting a departure to access ending everywhere, including on an already-open
/// WebSocket. This handler is the first link: it moves the row, and then writes the Redis
/// revocation set keyed by the employee's external subject, which is what the HTTP access gate and
/// the hub sweep both read.
/// </para>
/// <para>
/// The class holds no messaging types and no persistence types — it implements an Application port
/// and calls Application use cases. That is what lets it live in <c>InternalChat.Worker</c> without
/// naming Infrastructure outside the composition root (Principle I).
/// </para>
/// </remarks>
public sealed partial class DirectorySyncConsumer : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IUseCaseDispatcher _dispatcher;
    private readonly ILogger<DirectorySyncConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public DirectorySyncConsumer(IUseCaseDispatcher dispatcher, ILogger<DirectorySyncConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <inheritdoc />
    public string QueueName => "directory.sync";

    /// <inheritdoc />
    public async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        DirectoryChangePayload payload = Deserialize(envelope);

        SyncEmployeeResult result = await _dispatcher
            .SendAsync<SyncEmployee, SyncEmployeeResult>(
                new SyncEmployee(
                    payload.ExternalSubject,
                    ParseChange(payload.Change, envelope.MessageId),
                    payload.DisplayName,
                    payload.Email,
                    payload.AvatarUrl),
                cancellationToken)
            .ConfigureAwait(false);

        ChangeApplied(_logger, payload.Change ?? "(none)", payload.ExternalSubject, result.EmployeeId);
    }

    [LoggerMessage(
        EventId = 3500,
        Level = LogLevel.Information,
        Message = "Directory sync applied {Change} for {Subject} (employee {EmployeeId})")]
    private static partial void ChangeApplied(
        ILogger logger,
        string change,
        string subject,
        Guid employeeId);

    /// <summary>
    /// Reads the payload, failing loudly on anything unusable.
    /// </summary>
    /// <remarks>
    /// A throw here rolls back the deduplication record with it, so the message is genuinely
    /// unprocessed and retries — and after the capped retries lands in the dead-letter queue where
    /// it can be seen. Swallowing a malformed directory event would silently stop syncing one
    /// employee, and the visible symptom would be someone who left still having access.
    /// </remarks>
    private static DirectoryChangePayload Deserialize(MessageEnvelope envelope)
    {
        DirectoryChangePayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<DirectoryChangePayload>(envelope.Payload, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Directory event {envelope.MessageId} ({envelope.Type}) is not valid JSON.", ex);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.ExternalSubject))
        {
            throw new InvalidOperationException(
                $"Directory event {envelope.MessageId} ({envelope.Type}) carries no externalSubject. "
                + "That field is the join between a token and an employee row; without it there is "
                + "nothing to apply the change to.");
        }

        return payload;
    }

    private static DirectoryChange ParseChange(string? change, Guid messageId) => change switch
    {
        "upserted" => DirectoryChange.Upserted,
        "deactivated" => DirectoryChange.Deactivated,
        "reactivated" => DirectoryChange.Reactivated,

        // Not ignored. contracts/messaging.md requires consumers to tolerate unknown *fields*, not
        // unknown values of the field that decides what to do — treating an unrecognised change as
        // a no-op would mean a future "suspended" event silently left access in place.
        _ => throw new InvalidOperationException(
            $"Directory event {messageId} declares change '{change}', which this consumer does not "
            + "understand. See contracts/messaging.md for the permitted values."),
    };

    /// <summary>Wire shape of <c>chat.directory.employee.changed.v1</c>.</summary>
    private sealed record DirectoryChangePayload(
        string ExternalSubject,
        string? Change,
        string? DisplayName,
        string? Email,
        string? AvatarUrl);
}
