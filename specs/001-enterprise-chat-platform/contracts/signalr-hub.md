# Real-Time Hub Contract: `/hubs/chat`

**Version**: 1.1.0 | **Transport**: WebSockets (SignalR), Redis backplane

1.1.0 (feature 002, additive): `ConnectionInfo` event; delivery rules 4–7 below. Delta and rationale:
`specs/002-realtime-message-delivery/contracts/signalr-hub-delta.md`.

`ChatHub` contains no business logic. Every server method delegates to an Application use case
(Constitution Principle I). Every method declares an explicit authorization policy; an architecture
test fails the build when one does not.

## Connection lifecycle

| Stage | Behaviour |
| --- | --- |
| Connect | JWT validated. Revocation set checked. Connection joins a SignalR group per active conversation membership. |
| Reconnect | JWT re-validated and the revocation set re-checked — a long-lived connection MUST NOT outlive its token (D5). Client then calls `Resync`. |
| Token expiry mid-connection | Server sends `TokenExpiring` 60 s ahead; client refreshes silently. Failure to refresh closes the connection. |
| Membership change | Server moves the connection in or out of the group immediately, so a removed member stops receiving without needing to refresh (US3 scenario 3). |
| Disconnect | Presence and typing keys expire by TTL, never by explicit cleanup — a crashed client must not leave a permanent "typing" state. |

## Client → Server methods

| Method | Parameters | Authorization | Notes |
| --- | --- | --- | --- |
| `Resync` | `Dictionary<Guid conversationId, long lastSeenSeq>` | membership per entry | Returns everything above each `seq`. This is the mechanism behind FR-018 and SC-022: reconnect is a catch-up query, not a replay of a server-side buffer. |
| `StartTyping` | `conversationId` | membership | Writes a Redis key with a 10 s TTL. Deliberately fire-and-forget. |
| `StopTyping` | `conversationId` | membership | |
| `SetPresence` | `PresenceState` | self only | `online` \| `away` \| `dnd`. `offline` is inferred from disconnection, never asserted by a client. |

Messages are **sent over HTTP**, not over the hub. The hub is for delivery and ephemeral state.
This keeps the idempotent send path (FR-011) on one well-tested transport with clear status codes,
rather than duplicating retry semantics across two.

## Server → Client events

| Event | Payload | Triggered by |
| --- | --- | --- |
| `MessageReceived` | `Message` | A new message in a conversation the client belongs to (FR-009) |
| `MessageEdited` | `Message` | FR-014 |
| `MessageDeleted` | `{ conversationId, messageId, seq }` | FR-014 — body omitted deliberately |
| `ConversationCreated` | `Conversation` | Added to a new conversation (US3 scenario 1) |
| `ConversationUpdated` | `Conversation` | Name, visibility, or member count changed |
| `MembershipRevoked` | `{ conversationId }` | Removed — client drops the conversation immediately |
| `ReadStateUpdated` | `ReadState` | Read on another device (FR-036) |
| `TypingChanged` | `{ conversationId, employeeIds[] }` | FR-016 |
| `PresenceChanged` | `{ employeeId, presence }` | FR-017 |
| `AttachmentReady` | `Attachment` | Scan completed clean (FR-024) |
| `AttachmentRejected` | `{ attachmentId, reason }` | Scan found malware — uploader only (FR-024) |
| `MeetingStarted` | `Meeting` | FR-041 — shows the join prompt |
| `MeetingEnded` | `{ meetingId }` | FR-047 |
| `TokenExpiring` | `{ secondsRemaining }` | Access token nearing expiry |
| `ConnectionInfo` | `{ transport: "webSockets" \| "longPolling" }` | Once per connect and reconnect, to the caller only. Client shows a "limited connection" indicator when not `webSockets` (002 FR-010) |

## Delivery guarantees

- **At-least-once over the wire, exactly-once in effect.** Every event carries the message `seq`.
  A client that has already applied `seq` ignores a repeat. Combined with `Resync`, this is what
  makes SC-022 ("never lost, never duplicated") hold across reconnects and server restarts.
- **No ordering guarantee across conversations.** Within a conversation, `seq` is the order, and a
  client MUST apply by `seq` rather than by arrival time.
- **Events for the same message may arrive in any order** (1.1.0). `realtime.fanout` handles up to
  eight events at once, so `MessageReceived`, `MessageEdited`, and `MessageDeleted` for one id can
  overtake one another.
- **Tombstone rule** (1.1.0). After applying `MessageDeleted` for an id, a client MUST show any later
  `MessageReceived` or `MessageEdited` for that id as deleted.
- **Latest-version rule** (1.1.0). For the same id, a client MUST keep the copy with the greatest
  `editedAt` (null is oldest); a deleted copy beats any live one.
- **No batching** (1.1.0). The server sends each event as soon as it is processed and never holds
  events to coalesce them.
- **The hub is not durable.** An event missed while disconnected is recovered by `Resync`, never by
  server-side buffering. This is deliberate: buffering per connection would make memory a function
  of disconnected-client count, which breaks at 7,000 connections.

## Fan-out and scale

- Group name is `conv:{conversationId}`. Fan-out uses the Redis backplane.
- For conversations above 1,000 members, delivery is published from the Worker rather than inline in
  the sending request, so the sender's 150 ms accept budget is not a function of member count
  (US3 scenario 6, SC-013).
- Typing and presence are rate-limited server-side; a client flooding `StartTyping` is throttled,
  not disconnected.

## Failure behaviour

| Condition | Behaviour |
| --- | --- |
| Redis backplane unavailable | Delivery degrades to single-node; the client keeps working and `Resync` recovers on reconnect. No data loss (SC-024). |
| Client offline | Nothing is buffered. History and `Resync` cover the gap (FR-010). |
| Employee deactivated mid-connection | Revocation set check on the next hub invocation or heartbeat closes the connection within the 5-minute budget (FR-003, SC-018). |
