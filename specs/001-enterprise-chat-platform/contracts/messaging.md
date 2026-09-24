# Event Contracts (RabbitMQ)

**Version**: 1.0.0 | **Broker**: RabbitMQ 4 | **Client**: official `RabbitMQ.Client` (see
[research.md](../research.md) D3 — MassTransit is rejected as commercially licensed)

Every event here is written to the `outbox_message` table in the same transaction as the state
change it describes, then dispatched with publisher confirms (Constitution Principle VI). No
handler publishes inline.

## Topology

| Exchange | Type | Purpose |
| --- | --- | --- |
| `chat.events` | topic | All domain events |
| `chat.events.dlx` | topic | Dead letters from every queue |

Every queue is durable, uses manual acknowledgement, and declares
`x-dead-letter-exchange: chat.events.dlx` with a capped exponential retry (1 s, 5 s, 25 s, then
dead-letter). DLQ depth is alerted on — silent message loss is a Sev-1 defect.

| Queue | Binding | Consumer | Concurrency |
| --- | --- | --- | --- |
| `directory.sync` | `chat.directory.#` | Worker | 1 |
| `notifications.fanout` | `chat.message.sent.#` | Worker | 4 |
| `attachments.scan` | `chat.attachment.uploaded.#` | Worker | 2 |
| `search.index` | `chat.message.#` | Worker | 4 |
| `audit.write` | `chat.audit.#` | Worker | 2 |
| `realtime.fanout` | `chat.message.#` | API | 8 |
| `membership.fanout` | `chat.membership.#` | API | 4 |
| `meetings.lifecycle` | `chat.meeting.#` | Worker | 1 |

**Bindings use `#`, not `*`, and the difference is load-bearing.** AMQP's `*` matches exactly one
word; `#` matches zero or more. A routing key is the event type, and event types carry a version
suffix — so `chat.directory.employee.changed.v1` is five words and `chat.directory.*` matches none
of it.

An earlier revision of this table used `*` throughout and was wrong for four of the seven queues,
including `directory.sync`. That would have meant deactivations reached no consumer at all and
FR-003's five-minute revocation deadline was missed silently on every departure. Nothing would have
failed visibly: an exchange discards an unroutable message, the publisher confirm still succeeds,
and the outbox row is still marked dispatched. `tests/Integration/Messaging/TopologyRoutingTests.cs`
now publishes every known event type through a real broker and asserts which queues receive it, so
an unmatchable pattern fails the build instead of a deployment.

`realtime.fanout` and `search.index` bind every message event rather than sends alone: FR-014
requires an edit or a deletion to reach other clients in real time, and FR-032 requires it reflected
in search results.

⚠️ **Redeploying against an existing broker leaves the old bindings in place.** `QueueBind` adds a
binding rather than replacing one, so the superseded `*` patterns survive until they are deleted.
They match nothing and are harmless, but the runbook (T220) should include removing them so the
topology a person reads in the management UI is the topology in this table.

## Envelope

Every message carries the same envelope. Field names are stable across all event types.

```json
{
  "messageId": "0192f2c4-...",
  "type": "chat.message.sent.v1",
  "occurredAt": "2026-07-31T09:14:22.117Z",
  "traceId": "00-4bf92f...-01",
  "payload": { }
}
```

- `messageId` is the outbox row id and is the **idempotency key**. Consumers insert
  `(consumer_name, messageId)` into `processed_message` inside their handling transaction and skip
  anything already present. This is what makes at-least-once delivery safe.
- `traceId` is W3C trace context, propagated from the originating HTTP request through the outbox
  into the consumer, so a trace spans the whole path (Constitution observability requirement).
- `type` carries the version. Consumers MUST ignore unknown fields and MUST NOT fail on them.

## Events

### `chat.directory.employee.changed.v1`

Routing key: `chat.directory.employee.changed`

```json
{
  "externalSubject": "dev-an.nguyen",
  "change": "upserted|deactivated|reactivated",
  "displayName": "Nguyễn Thị Vân An",
  "email": "an.nguyen@internalchat.local",
  "avatarUrl": null,
  "occurredAt": "2026-08-09T09:14:22.117Z"
}
```

The corporate directory is authoritative for employee lifecycle (FR-001); this platform only
projects it. Consumed by `directory.sync`, which upserts the `employee` row and, on
`deactivated`, writes the Redis revocation set — the path with the tightest deadline in the system
(FR-003, five minutes).

`externalSubject` is the key, not an internal id. The producer is outside this platform and has no
way to know one; it is also the `sub` claim every token carries, which is what makes the join
possible at all.

**`displayName` and `email` are ignored for `deactivated` and `reactivated`.** A rename and a
revocation must not be the same operation when only one of them has a deadline attached — the
domain enforces this too (`Employee.UpdateDirectoryAttributes` never touches `Status`).

Concurrency is **1**, deliberately. Ordering matters here in a way it does not elsewhere: a
deactivation overtaken by a stale upsert would silently restore access.

### `chat.message.sent.v1`

Routing key: `chat.message.sent.{conversationKind}`

```json
{
  "conversationId": "uuid",
  "conversationKind": "direct|group",
  "messageId": "uuid",
  "seq": 48213,
  "authorId": "uuid",
  "sentAt": "2026-07-31T09:14:22.117Z",
  "mentions": ["uuid"],
  "hasAttachments": true,
  "recipientCount": 12
}
```

**Deliberately excludes the message body.** Consumers that need it read from PostgreSQL. Message
bodies in queue payloads would end up in broker logs, management UI, and DLQ dumps, which
FR-056 forbids.

Consumed by `notifications.fanout` (decide who to notify, honour mute and DND, batch backlog),
`search.index` (update `body_tsv`), and `realtime.fanout` (large-conversation delivery).

### `chat.message.edited.v1` / `chat.message.deleted.v1`

```json
{ "conversationId": "uuid", "messageId": "uuid", "seq": 48213, "actorId": "uuid" }
```

Drives search reindexing so FR-032 holds — content a user can no longer access stops appearing.

### `chat.membership.changed.v1`

```json
{
  "conversationId": "uuid",
  "employeeId": "uuid",
  "change": "added|removed|role_changed",
  "actorId": "uuid",
  "visibleFromSeq": 48000
}
```

**Correction (T116).** This section previously said the API's real-time fan-out, `search.index`, and
`audit.write` all consumed this event. Only the first is true today:

- **Real-time fan-out** (`membership.fanout`, API, T116): moves the affected employee's local
  connections in or out of the conversation's SignalR group immediately, and sends
  `ConversationCreated` (on add) or `MembershipRevoked` (on remove) so a client already connected
  reflects the change without reconnecting (US3 scenarios 1 and 3).
- **Audit** is not consumed from this event. `AddMember`/`RemoveMember` implement
  `IAuditableRequest`, so the audit record commits synchronously inside the same transaction as the
  membership row (T072's pattern) — the same reason `chat.audit.*` has no producer yet (see
  `audit.write`'s note above).
- **Search reindexing** (dropping a removed employee's access to search results, FR-032) is US6's
  job and has no consumer yet. `search.index`'s binding (`chat.message.#`) does not match this event
  type; T167 is where that gap closes.

### `chat.attachment.uploaded.v1`

```json
{ "attachmentId": "uuid", "conversationId": "uuid", "objectKey": "...", "kind": "image|video", "byteSize": 12345 }
```

Consumed by `attachments.scan`: ClamAV scan, poster-frame extraction for video, promotion from the
quarantine bucket, then `chat.attachment.scanned.v1`.

### `chat.attachment.scanned.v1`

```json
{ "attachmentId": "uuid", "conversationId": "uuid", "verdict": "clean|infected|failed", "scannedAt": "..." }
```

`clean` makes the attachment retrievable and emits `AttachmentReady` over SignalR. `infected` purges
the object, notifies only the uploader, and writes an audit event (FR-024).

### `chat.read_state.updated.v1`

```json
{ "employeeId": "uuid", "conversationId": "uuid", "lastReadSeq": 48213 }
```

Drives cross-device unread consistency (FR-036).

### `chat.meeting.started.v1` / `chat.meeting.ended.v1`

```json
{ "meetingId": "uuid", "conversationId": "uuid", "actorId": "uuid", "at": "...", "peakParticipants": 9 }
```

Consumed by `meetings.lifecycle` (reconcile the platform-wide participant counter) and
`audit.write` (FR-051).

### `chat.audit.recorded.v1`

```json
{
  "action": "membership.removed",
  "actorId": "uuid",
  "subjectType": "conversation",
  "subjectId": "uuid",
  "sourceIp": "10.0.0.5",
  "outcome": "success",
  "detail": { }
}
```

`detail` never contains message bodies, attachment contents, or credentials (FR-056).

### `chat.retention.swept.v1`

```json
{ "sweptThrough": "2025-07-31", "messagesDeleted": 10422318, "attachmentsDeleted": 88214, "partitionsDropped": ["messages_2025_07"] }
```

Audited per FR-052.

## Consumer requirements

Every consumer MUST:

1. Be idempotent on `messageId` via `processed_message`, inside the handling transaction.
2. Acknowledge only after its work is committed.
3. Treat unknown envelope fields as ignorable — never fail on them.
4. Complete within 30 seconds or move the work to a follow-up message.
5. Emit its own audit event where the action is security-relevant.
6. Assume **no ordering between queues**. Where per-conversation ordering matters, `seq` in the
   payload is the authority, not arrival order.

## Backward-compatibility tests

`tests/Contract/` asserts, for every event type, that: the current producer's payload validates
against the committed v1 schema; no field has been removed or narrowed; and a consumer fed a
payload with an unknown extra field still succeeds. A change that breaks any of these fails CI
(Constitution CI gate 5).
