# Hub Contract Delta: `/hubs/chat` 1.0.0 → 1.1.0

**Base**: `specs/001-enterprise-chat-platform/contracts/signalr-hub.md` (1.0.0)
**Change type**: additive, backward-compatible (Principle VI). A 1.0.0 client ignores the new event
and keeps working; it simply does not get the degraded-connection indicator.

## Added server → client event

| Event | Payload | Triggered by |
| --- | --- | --- |
| `ConnectionInfo` | `{ transport: "webSockets" \| "longPolling" }` | Sent once by `OnConnectedAsync`, i.e. after every connect and reconnect, before any other event on that connection |

- `transport` is the transport the server actually negotiated for this connection
  (`IHttpTransportFeature`), not what the client asked for.
- Authorization: same as connecting. Sent only to the connecting connection (`Clients.Caller`).
- Contains nothing sensitive; it is not logged with any employee identifier.

Client behaviour: if `transport !== "webSockets"`, show a non-blocking "Limited connection —
messages may arrive more slowly" indicator (spec FR-010). Clear it on the next `ConnectionInfo`
reporting `webSockets`.

## Clarified delivery guarantees (normative for clients)

Added to **Delivery guarantees**. These make explicit what "apply by `seq`, not arrival" already
implies, now that `realtime.fanout` processes up to 8 events concurrently (research R3).

1. **Events for the same message may arrive in any order.** `MessageReceived`, `MessageEdited`, and
   `MessageDeleted` for one `messageId` are not guaranteed to arrive in the order they happened.
2. **Tombstone rule.** After applying `MessageDeleted` for an id, a client MUST ignore any later
   `MessageReceived` or `MessageEdited` for that id.
3. **Latest-version rule.** For the same id, a client MUST keep the payload with the greatest
   `editedAt` (absent/null is older than any value). A `MessageReceived` arriving after a
   `MessageEdited` MUST NOT restore the pre-edit text.
4. **No batching.** The server sends each event as soon as it is processed; it never holds events
   to coalesce them (spec FR-003).

## Updated latency statement

Replaces the implicit feature 001 target for fan-out:

| Path | p95 | p99 |
| --- | --- | --- |
| Commit of an accepted message → `MessageReceived` sent to the group (server-side, `chat.delivery.fanout_lag`) | 150 ms | 300 ms |
| Sender presses send → message visible to online recipient (end-to-end) | 300 ms | 500 ms |
