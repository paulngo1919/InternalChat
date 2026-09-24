namespace InternalChat.Api.Contracts;

/// <summary><c>PUT /conversations/{id}/read-state</c> body.</summary>
public sealed record MarkReadRequest(long LastReadSeq);

/// <summary>
/// <c>ReadState</c> in openapi.yaml — the effective position after the monotonic merge (FR-036).
/// </summary>
public sealed record ReadStateResponse(Guid ConversationId, long LastReadSeq, int UnreadCount);

/// <summary><c>PUT /conversations/{id}/mute</c> body (FR-037).</summary>
public sealed record MuteConversationRequest(DateTimeOffset? MutedUntil);

/// <summary>
/// <c>NotificationPreferences</c> in openapi.yaml (FR-037, FR-038).
/// </summary>
/// <param name="DndStart">
/// <c>"HH:mm"</c>, matching the contract's example, or <c>null</c> for no window. Not a bare
/// <see cref="TimeOnly"/> serialization, which System.Text.Json would render as
/// <c>"18:00:00.0000000"</c> — a format the contract does not document and a client parsing
/// <c>"HH:mm"</c> would fail on.
/// </param>
public sealed record NotificationPreferencesResponse(
    string? DndStart,
    string? DndEnd,
    string TimeZone,
    int DigestAfterMinutes);

/// <summary><c>POST /notifications/subscriptions</c> body (FR-034).</summary>
public sealed record PushSubscriptionRequest(string Endpoint, string P256dh, string Auth);
