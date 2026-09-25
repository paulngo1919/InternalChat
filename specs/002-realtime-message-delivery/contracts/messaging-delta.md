# Messaging Contract Delta

**Base**: `specs/001-enterprise-chat-platform/contracts/messaging.md`
**Change type**: operational only. No event type, payload, routing key, or queue binding changes.
All `*.v1` contracts are untouched.

## PostgreSQL notification channel (new, internal)

| Channel | Payload | Emitted by | Listened by | Semantics |
| --- | --- | --- | --- | --- |
| `outbox_ready` | empty string | trigger `outbox_message_notify` (statement-level, `AFTER INSERT`) | Worker `OutboxDispatcherService` | "At least one outbox row committed since you last looked." Delivered only on commit. Carries no data; the dispatcher always re-reads the table. Losing a notification is safe: the backstop poll drains the table within `IdlePollInterval` (5 s). |

## Dispatcher behaviour (changed)

| Aspect | 1.0 | 1.1 |
| --- | --- | --- |
| Wake-up | Fixed 1 s sleep after a non-full batch | Immediate on `outbox_ready`; 5 s backstop poll |
| Channel | New confirming channel per batch | One long-lived confirming channel, recreated on failure |
| Publish | Serial, one confirm awaited per row | Pipelined; bounded outstanding confirms; row marked on its own confirm |
| Guarantees | At-least-once, never marked before confirm | Unchanged |

## Queue consumption (changed)

| Queue | Prefetch | Dispatch concurrency 1.0 → 1.1 | Why |
| --- | --- | --- | --- |
| `realtime.fanout` | 8 | 1 → 8 | Throughput under burst (research R3). Safe because clients apply by `seq` with tombstone and latest-version rules (hub contract 1.1.0). |
| all other queues | unchanged | 1 (unchanged) | `directory.sync` ordering is a security property; others are not latency-critical. |

Idempotency (`processed_message`, `ON CONFLICT DO NOTHING`), DLQ, and bounded retry are unchanged
and remain correct under concurrency.
