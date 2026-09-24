using System.Text.Json;
using InternalChat.Application.Abstractions;
using InternalChat.Domain.Common;
using InternalChat.Domain.Conversations;
using InternalChat.Domain.Messages;
using InternalChat.Domain.Notifications;
using InternalChat.Worker.Notifications;

namespace InternalChat.Worker.Consumers;

/// <summary>
/// T133 — decides who gets a push notification for a message, honouring mute, do-not-disturb, and
/// the mention-only rule for groups (FR-034, FR-035, FR-037).
/// </summary>
/// <remarks>
/// <para>
/// <b>Direct messages always notify; group messages notify only the mentioned</b> (US4 scenario 2,
/// FR-035). A busy group's ordinary traffic would otherwise page every member on every message,
/// which is the exact flood FR-035 forbids.
/// </para>
/// <para>
/// <b>The digest suppression is one Redis key per employee, not per conversation.</b> The first
/// eligible message after a quiet period sends immediately and starts a
/// <see cref="NotificationPreference.DigestAfterMinutes"/>-long cooldown; anything else that would
/// have notified the same employee during that window is suppressed instead of queued, and
/// <c>DigestJob</c> (T135) catches the accumulated backlog once the cooldown lapses. Per employee
/// rather than per conversation because FR-038's threshold is a property of how long *the person*
/// has been unreachable, not of any one conversation.
/// </para>
/// <para>
/// Not dispatched through <see cref="IUseCaseDispatcher"/>, matching
/// <c>RealtimeFanoutConsumer</c>'s precedent: this is a multi-recipient side effect, not a single
/// state-mutating use case, and per-recipient failures (a dead subscription, an unreachable push
/// service) must not roll back the ones that already succeeded.
/// </para>
/// </remarks>
public sealed partial class NotificationFanoutConsumer : IMessageConsumer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IConversationRepository _conversations;
    private readonly IMembershipRepository _memberships;
    private readonly IMessageRepository _messages;
    private readonly INotificationPreferenceRepository _preferences;
    private readonly IPushSubscriptionStore _subscriptions;
    private readonly IPushSender _sender;
    private readonly ICacheStore _cache;
    private readonly IClock _clock;
    private readonly ILogger<NotificationFanoutConsumer> _logger;

    /// <summary>Creates the consumer.</summary>
    public NotificationFanoutConsumer(
        IConversationRepository conversations,
        IMembershipRepository memberships,
        IMessageRepository messages,
        INotificationPreferenceRepository preferences,
        IPushSubscriptionStore subscriptions,
        IPushSender sender,
        ICacheStore cache,
        IClock clock,
        ILogger<NotificationFanoutConsumer> logger)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _conversations = conversations;
        _memberships = memberships;
        _messages = messages;
        _preferences = preferences;
        _subscriptions = subscriptions;
        _sender = sender;
        _cache = cache;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string QueueName => "notifications.fanout";

    /// <inheritdoc />
    public async Task HandleAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        MessageSentPayload payload = Deserialize(envelope);

        Conversation? conversation = await _conversations
            .FindAsync(payload.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        if (conversation is null)
        {
            // Gone by the time this was processed. Nothing to notify anyone about.
            return;
        }

        IReadOnlyList<Membership> members = await _memberships
            .ListForConversationAsync(payload.ConversationId, cancellationToken)
            .ConfigureAwait(false);

        HashSet<Guid> mentioned = [.. payload.Mentions ?? []];

        IReadOnlyList<Membership> recipients =
        [
            .. members.Where(m =>
                m.IsActive
                && m.EmployeeId != payload.AuthorId
                && (conversation.Kind == ConversationKind.Direct || mentioned.Contains(m.EmployeeId))),
        ];

        if (recipients.Count == 0)
        {
            return;
        }

        Message? message = await _messages
            .FindAsync(payload.ConversationId, payload.MessageId, payload.SentAt, cancellationToken)
            .ConfigureAwait(false);

        if (message is null || message.IsDeleted)
        {
            // Deleted (or gone) between send and fan-out. FR-039 forbids a notification revealing
            // content that has since become unavailable, and there is nothing else worth pushing.
            return;
        }

        PushPayload notification = new(
            Title: "New message",
            Body: Truncate(message.Body?.Value ?? string.Empty),
            DeepLink: $"/conversations/{payload.ConversationId}/messages/{payload.MessageId}");

        foreach (Membership recipient in recipients)
        {
            await NotifyOneAsync(recipient, notification, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task NotifyOneAsync(
        Membership recipient,
        PushPayload notification,
        CancellationToken cancellationToken)
    {
        if (recipient.MutedUntil is { } mutedUntil && mutedUntil > _clock.UtcNow)
        {
            return;
        }

        NotificationPreference preference = await _preferences
            .FindAsync(recipient.EmployeeId, cancellationToken)
            .ConfigureAwait(false)
            ?? NotificationPreference.Default(recipient.EmployeeId);

        if (preference.DoNotDisturb.IsActiveAt(_clock.UtcNow))
        {
            return;
        }

        string cooldownKey = NotificationCooldown.KeyFor(recipient.EmployeeId);

        if (await _cache.GetAsync<string>(cooldownKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            // Already notified within the digest window — DigestJob sweeps up whatever arrives
            // during it rather than this consumer sending one push per message (FR-038).
            return;
        }

        await _cache
            .SetAsync(cooldownKey, "1", TimeSpan.FromMinutes(preference.DigestAfterMinutes), cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<PushSubscriptionDescriptor> subscriptions = await _subscriptions
            .ListForEmployeeAsync(recipient.EmployeeId, cancellationToken)
            .ConfigureAwait(false);

        foreach (PushSubscriptionDescriptor subscription in subscriptions)
        {
            PushDeliveryResult result = await _sender
                .SendAsync(subscription, notification, cancellationToken)
                .ConfigureAwait(false);

            switch (result)
            {
                case PushDeliveryResult.Delivered:
                    await _subscriptions
                        .MarkDeliveredAsync(subscription.SubscriptionId, _clock.UtcNow, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case PushDeliveryResult.SubscriptionExpired:
                    await _subscriptions
                        .RemoveAsync(subscription.SubscriptionId, cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case PushDeliveryResult.TransientFailure:
                    // Left in place. The next eligible message retries it, and a subscription that
                    // never recovers eventually gets pruned by a real expiry response instead.
                    TransientDeliveryFailure(_logger, subscription.SubscriptionId);
                    break;
            }
        }
    }

    /// <summary>Short preview, never the full body — a push notification is a summary, not a copy.</summary>
    private static string Truncate(string body)
    {
        const int MaxLength = 200;
        return body.Length <= MaxLength ? body : string.Concat(body.AsSpan(0, MaxLength), "…");
    }

    private static MessageSentPayload Deserialize(MessageEnvelope envelope) =>
        JsonSerializer.Deserialize<MessageSentPayload>(envelope.Payload, SerializerOptions)
        ?? throw new InvalidOperationException(
            $"Message {envelope.MessageId} of type '{envelope.Type}' carried an unreadable payload.");

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Warning,
        Message = "Push delivery to subscription {SubscriptionId} failed transiently and was left in place")]
    private static partial void TransientDeliveryFailure(ILogger logger, Guid subscriptionId);
}

/// <summary>The queue payload for <c>chat.message.sent.v1</c> — no body, per contracts/messaging.md.</summary>
internal sealed record MessageSentPayload(
    Guid ConversationId,
    Guid MessageId,
    long Seq,
    Guid AuthorId,
    DateTimeOffset SentAt,
    IReadOnlyList<Guid>? Mentions);
