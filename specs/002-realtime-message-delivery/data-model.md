# Data Model: Instant Message Delivery

**Feature**: `002-realtime-message-delivery` | **Date**: 2026-09-25

No new tables and no new domain entities. The changes are one database trigger, a change in how
existing rows move through time, and in-memory / metric shapes.

## 1. `outbox_message` — wake-up trigger (migration)

Existing table from feature 001 (`20260809025908_OutboxAndProcessedMessage`). Unchanged columns.

**Added**: a statement-level trigger.

| Object | Definition |
| --- | --- |
| Function `outbox_notify()` | `RETURNS trigger`, body `PERFORM pg_notify('outbox_ready', ''); RETURN NULL;` |
| Trigger `outbox_message_notify` | `AFTER INSERT ON outbox_message FOR EACH STATEMENT EXECUTE FUNCTION outbox_notify()` |

**Constraints and properties**

- Fires once per `INSERT` statement, not per row; PostgreSQL further collapses identical
  notifications within one transaction. One send → at most one wake-up.
- Delivered to listeners only on `COMMIT`; discarded on `ROLLBACK`.
- Empty payload by design: the channel is a doorbell, not a data path. No ids, no content (FR-056,
  Principle IV).
- Migration is forward-only and live-safe: creating a function and trigger takes a brief
  `SHARE ROW EXCLUSIVE` lock on `outbox_message` only; the previous application version ignores
  notifications and keeps polling. `Down` drops trigger then function.

## 2. Outbox row lifecycle (timing change only)

States are unchanged: `pending (dispatched_at IS NULL, attempts < max)` → `dispatched` or
`stuck (attempts ≥ max)`.

| Transition | Before | After |
| --- | --- | --- |
| pending → claimed | Next poll, up to 1 s after commit | On `outbox_ready` notification (≈ 1 ms), or the 5 s backstop poll |
| claimed → dispatched | Each row confirmed serially | Rows published pipelined; each marked on its **own** confirm |

Invariant kept: a row is never marked dispatched before its broker confirm.

## 3. Dispatcher wake signal (in-memory, Worker)

| Field | Type | Notes |
| --- | --- | --- |
| signal | single-slot, coalescing (`SemaphoreSlim(0,1)` style) | Many notifications between passes → one extra pass. Never unbounded. |
| listener state | `connected` \| `reconnecting` | While `reconnecting`, the loop relies on the backstop poll. Reconnect with exponential backoff capped at 30 s. |

## 4. Client message store rules (browser, per conversation)

Extends the existing `seq`-keyed store in `ConversationView`.

| Field | Type | Rule |
| --- | --- | --- |
| `byId` | `Map<messageId, Message>` | Replace an entry only when the incoming `editedAt` is greater (null = oldest). |
| `tombstones` | `Set<messageId>`, bounded (e.g. last 500 per conversation) | Added on `MessageDeleted`; any later `MessageReceived`/`MessageEdited` for that id is dropped. |
| `lastSeenSeq` | `number` | Unchanged; drives `Resync`. |

Send state for the sender's own messages (existing, now specified): `sending` → `sent` | `failed(reason)`.
A `failed` message is never retried silently and never becomes visible to others.

## 5. Connection info (browser, per connection)

| Field | Type | Source |
| --- | --- | --- |
| `transport` | `'webSockets' \| 'longPolling'` | `ConnectionInfo` hub event (contracts/signalr-hub.md) |
| `degraded` | `boolean` | `transport !== 'webSockets'` → shows the limited-connection indicator |

## 6. Delivery measurement (metrics only — no rows)

| Instrument | Kind | Unit | Tags | Buckets (ms) |
| --- | --- | --- | --- | --- |
| `chat.delivery.outbox_lag` | Histogram | ms | `event_type` | 5, 10, 25, 50, 100, 200, 300, 500, 1000, 2000, 5000 |
| `chat.delivery.fanout_lag` | Histogram | ms | `event_type` | same |
| `chat.delivery.client_lag` | Histogram | ms | `transport` | same |
| `chat.hub.connections` | UpDownCounter | {connection} | `transport` | — |

- `outbox_lag` = broker confirm time − `outbox_message.occurred_at`.
- `fanout_lag` = hub `SendAsync` completion − message `sent_at` (both server clocks).
- `client_lag` = sum of bucket counts reported by clients (see contracts `delivery-telemetry`).
- **No** conversation id, message id, or employee id on any instrument (cardinality and privacy).
- Retained in Prometheus 30 days (SC-009), aggregated per minute by the scrape interval.

## 7. Configuration changes

| Setting | Before | After | Where |
| --- | --- | --- | --- |
| `RabbitMq:IdlePollInterval` | 1 s | 5 s (backstop only) | `RabbitMqOptions` default |
| `RabbitMq:FanoutConsumerConcurrency` | — (1) | 8 | new option, `realtime.fanout` only |
| Npgsql `Minimum Pool Size` | 0 | API 4, Worker 2 | connection strings in compose / `.env.example` |
| Prometheus `--storage.tsdb.retention.time` | 15d | 30d | `deploy/docker-compose.observability.yml` |
