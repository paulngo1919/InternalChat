# Research: Instant Message Delivery

**Feature**: `002-realtime-message-delivery` | **Date**: 2026-09-25

This feature adds no new product capability. It removes latency from the path feature 001 already
built. Every decision below is grounded in reading that path as it exists on `main`.

## R0. Where the delay comes from (as-built analysis)

The send-to-display path today:

```text
Browser ──HTTP POST──▶ API: SendMessage use case
                         └─ one transaction: message row + outbox_message row  ── commit, 201 to sender
Worker: OutboxDispatcherService loop
   ├─ claim batch (FOR UPDATE SKIP LOCKED), open a NEW confirming channel, publish rows ONE BY ONE,
   │  awaiting each broker confirm, mark dispatched, commit
   └─ if batch < 100 rows  ──▶  Task.Delay(IdlePollInterval = 1 s)          ◀── dominant cost
RabbitMQ: chat.events ──▶ realtime.fanout queue (prefetch 8)
API: ConsumerHost ─ serial handling (dispatch concurrency 1) per delivery:
   BEGIN · INSERT processed_message · SELECT message · hub SendAsync · SaveChanges · COMMIT · ack
SignalR (Redis backplane) ──▶ every connection in group conv:{id} ──▶ client applies by seq
```

| # | Cost | Size | When it bites |
| --- | --- | --- | --- |
| C1 | Dispatcher sleeps a fixed 1 s after any non-full batch (`RabbitMqOptions.IdlePollInterval`) | 0–1000 ms, **mean ≈ 500 ms per message** at normal traffic | Every message except during sustained >100-per-pass bursts. This is the reported delay. It alone makes feature 001 SC-006 (p95 500 ms) statistically unreachable. |
| C2 | New AMQP channel + confirm-select per batch | 1–3 ms per batch | Always, small |
| C3 | Sequential publish, each awaiting its own persistent confirm (broker fsync) | ~1–5 ms × rows in batch | Bursts: a 100-row batch costs 100 serial fsync round trips |
| C4 | Fan-out consumer is serial: ~5 DB round trips + backplane publish per message | ~3–8 ms per message → ~150–300 msg/s ceiling per API replica | Bursts (feature 001 SC-012: 1,000 msg/s) queue behind each other; head-of-line latency grows to seconds |
| C5 | Cold database connections after idle (Npgsql prunes idle pooled connections) | 10–50 ms on the first message after a quiet period | Idle-then-send (spec FR-002) |
| C6 | Client falls back to Long Polling when WebSockets cannot be negotiated | +1 HTTP round trip per poll cycle, typically 50–300 ms | Restrictive networks, misconfigured proxies (dev Vite proxy included) |

The existing Playwright probe (`tests/e2e/v2-delivery-timing.spec.ts`) sends 20 samples in quick
succession, so each sample lands in a random phase of the 1 s sleep. C1 alone predicts a p95 of
≈ 950 ms there.

## R1. Waking the outbox dispatcher on commit

**Decision**: PostgreSQL `LISTEN`/`NOTIFY`. A statement-level `AFTER INSERT` trigger on
`outbox_message` calls `pg_notify('outbox_ready', '')`. The dispatcher holds one dedicated,
non-pooled Npgsql connection that `LISTEN outbox_ready` and wakes its loop on any notification. The
existing timed poll stays as a safety net, with the idle interval raised from 1 s to 5 s.

**Rationale**:

- `NOTIFY` is transactional: PostgreSQL delivers it **only when the inserting transaction commits**,
  and never for a rolled-back one. The wake-up therefore cannot outrun the row it announces, and a
  rejected send (spec FR-009) produces no signal at all. This preserves the outbox guarantee
  (Principle VI) exactly.
- Notifications with an identical channel and payload inside one transaction are collapsed, so a
  send that writes several outbox rows (message + attachment events) wakes the dispatcher once.
- Wake latency is one PostgreSQL round trip (≈ 1 ms on the same host), removing C1 entirely.
- Zero new dependencies: Npgsql already exposes `NpgsqlConnection.Notification` and `WaitAsync`.
  Principle VIII holds.
- Works across processes. The sender's request is in the API; the dispatcher is in the Worker. An
  in-process signal would not reach it.
- Loss of the listener connection is harmless for correctness: the 5 s poll still drains the table,
  and the listener reconnects with backoff. The failure mode is "slower", never "lost" (SC-010).

**Alternatives considered**:

| Alternative | Rejected because |
| --- | --- |
| Lower `IdlePollInterval` to 20–50 ms | Still adds 10–25 ms mean latency; 20–50 idle queries/s per Worker forever; does not scale down cleanly with more Worker replicas. Kept only as the backstop, at 5 s. |
| API publishes to RabbitMQ directly after commit, poller as backstop | Fastest (skips one hop), but duplicates publish logic, confirm handling, and failure accounting in a second process, and races the poller for the same rows. More moving parts for ~1 ms. Revisit only if R1 measurements miss the budget. |
| Publish inside the request transaction (dual write) | Prohibited by Principle VI. |
| Debezium / logical-replication CDC | New runtime component, operational weight far beyond the problem. |
| Redis pub/sub as the wake signal | Signal is not tied to commit; a wake could precede visibility of the row or announce a rolled-back one. Adds a Redis dependency to a path Principle VII says must survive Redis loss. |

## R2. Dispatcher publish efficiency

**Decision**: Keep one long-lived confirming channel per dispatcher (recreated only on failure), and
publish a claimed batch with **pipelined** confirms: issue every `BasicPublishAsync` for the batch,
then await all confirms, marking each row dispatched only when its own confirm arrives. Bound the
outstanding confirms with RabbitMQ.Client 7's publisher-confirmation tracking
(`CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true,
outstandingPublisherConfirmationsRateLimiter: …)`).

**Rationale**: Removes C2 and turns C3 from N serial broker fsyncs into roughly one, because the
broker batches fsyncs for concurrently outstanding persistent messages. Per-row confirm semantics are
unchanged: a row is never marked dispatched before its confirm (the existing invariant in
`OutboxDispatcher`). Ordering within `chat.events` is preserved by the channel; consumers already
order by `seq`, not arrival.

**Alternatives considered**: transient (non-persistent) messages for `realtime.fanout`. Rejected
because the exchange is shared with notification, search, and audit queues that need persistence,
and splitting exchanges is a contract change for a few milliseconds.

## R3. Fan-out consumer throughput and ordering

**Decision**: Raise dispatch concurrency on the `realtime.fanout` consumer only, to match its
prefetch (8). Make the client robust to the reorderings that concurrency makes possible, which are
already allowed by the hub contract ("apply by `seq`, not arrival"). Two client rules, added to the
contract:

1. **Tombstone rule.** A `MessageDeleted` for an id records a tombstone; a later
   `MessageReceived`/`MessageEdited` for that id is ignored.
2. **Latest-version rule.** For the same message id, the client keeps the payload with the greatest
   `editedAt` (null treated as oldest). A `MessageReceived` arriving after a `MessageEdited` never
   restores the older text.

**Rationale**: C4 caps a replica at a few hundred deliveries per second when serial. Per-message
work is dominated by I/O waits, so 8 concurrent handlers raise the ceiling roughly eightfold with no
extra hardware. The consumer's idempotency guard (`processed_message`) is unaffected: it is a
per-message insert with `ON CONFLICT DO NOTHING`, safe under concurrency. The consumer already reads
the stored row, so what it delivers is the current state; the only new hazard concurrency adds is
arrival order, which the two client rules neutralise. Other queues keep concurrency 1, in
particular `directory.sync`, whose ordering is a security property.

**Alternatives considered**:

| Alternative | Rejected because |
| --- | --- |
| Per-conversation partitioned queues (consistent-hash exchange) | Plugin not in the base image; fixed partition count; strict per-conversation order that the client does not need. |
| Drop the `processed_message` transaction for realtime fan-out | Saves ~2 round trips but weakens a Principle VI rule for an idempotency the client already provides. Not worth a constitutional exception before measurement shows need. |
| Put the message body in the queue payload to skip the read-back | Prohibited by feature 001 FR-056 (no bodies in broker logs / DLQ). |

## R4. Idle-period warm paths

**Decision**: Set `Minimum Pool Size` on the Npgsql connection strings for the API (e.g. 4) and the
Worker (e.g. 2), so the first query after a quiet period does not pay connection setup (C5). The
dispatcher's LISTEN connection is outside the pool and permanently open by design. RabbitMQ
connections are already long-lived; the channel reuse from R2 removes the remaining per-batch setup.

**Rationale**: Directly targets spec FR-002 / SC-006 (first message after ≥ 10 idle minutes).
Configuration only; no code or dependency.

## R5. Degraded transport visibility and prevention

**Decision**:

- The hub sends a `ConnectionInfo` event right after connect and reconnect carrying the negotiated
  transport (`webSockets` | `longPolling`), read server-side from `IHttpTransportFeature`. The
  client shows a non-blocking "Limited connection — messages may be slower" indicator when it is not
  `webSockets` (spec FR-010).
- A startup log warning and a metric label record the transport per connection, so a proxy that
  silently strips `Upgrade` shows up in operations dashboards, not in employee complaints.
- The dev Vite proxy for `/hubs` MUST set `ws: true`; the production Nginx `/hubs/` location
  already forwards `Upgrade` (`deploy/nginx/nginx.conf`).

**Alternatives considered**: reading the transport from the SignalR JS client's internals.
Rejected: not a public API and changes between versions.

## R6. Measuring delivery time

**Decision**: Three layers, each answering a different question.

| Layer | Measures | Where | Satisfies |
| --- | --- | --- | --- |
| Server stage histograms | `chat.delivery.outbox_lag` (commit → broker confirm), `chat.delivery.fanout_lag` (commit → hub `SendAsync` returned), both in ms, tagged by event type only | OpenTelemetry `Meter` (already wired) → Collector → Prometheus | FR-011, SC-009 |
| Client-observed lag | `receivedAt − sentAt` per delivered message, with clock offset estimated from the `Date` header of API responses; reported in aggregated buckets every 60 s via one `POST /api/v1/telemetry/delivery` | Browser → API → same Meter | FR-011 (user-perceived), SC-011 evidence |
| Release gate | Wall-clock send-to-visible across two real browsers on one machine (no clock skew), including an idle-then-send case | Playwright `v2-delivery-timing.spec.ts` (tightened) + new idle spec; k6 for load | FR-013, SC-001–SC-007 |

Prometheus retention rises from 15 d to 30 d (SC-009). A Prometheus alerting rule fires when
`histogram_quantile(0.95, chat_delivery_fanout_lag)` exceeds 300 ms for 5 minutes (FR-012); Grafana
(already deployed) evaluates and displays it, so no new component is added.

**Rationale**: The server histogram is precise but blind to the browser and network; the client
report sees what the employee sees but is skew-prone; the Playwright gate is exact but synthetic.
Together they give detection (alert), diagnosis (stage breakdown), and prevention (gate).

**Privacy**: no layer records message content, conversation ids, or employee ids in metrics
(Principle IV). The client report carries only bucket counts.

**Alternatives considered**: per-message client acknowledgement back to the server. Rejected:
doubles hub traffic at 7,000 connections for data that aggregate buckets provide.

## R7. Sender-side responsiveness (Story 2)

**Finding**: Already largely in place. `ConversationView` renders an optimistic bubble keyed by
`clientMessageKey`, reconciles it with the HTTP response, and the sender's other devices are members
of the same SignalR group, so they receive `MessageReceived` through fan-out. No design change;
the plan adds a measurement (SC-002) and tests for the failed-state and no-flicker behaviours.

## Dependency register

No new dependencies. Everything used is already in the stack and licence register of feature 001:
Npgsql (PostgreSQL License), RabbitMQ.Client (Apache-2.0 / MPL-2.0), OpenTelemetry .NET (Apache-2.0),
Prometheus (Apache-2.0), Grafana (AGPL-3.0, self-hosted and unmodified), Playwright (Apache-2.0),
k6 (AGPL-3.0, test-time only).

## Measurements

### Baseline (T001) — 2026-09-25, `main` @ f35ae34

Browser baseline (`v2-delivery-timing`) **not yet run**: the full Compose stack (API, web, nginx)
was not up in the implementation session. Server-side baseline instead, from
`OutboxWakeLatencyTests.Without_the_listener_the_backstop_poll_still_drains_every_row`, which runs
the dispatcher exactly as `main` did — no listener, 1 s poll — with samples at random phases:

| Path | p50 | max | n |
| --- | --- | --- | --- |
| Outbox commit → `realtime.fanout` (poll only, pre-002) | 233 ms | 537 ms | 8 |

Consistent with R0/C1: uniform 0–1,000 ms wait, mean ≈ 500 ms (n = 8 is small).

### After US1 (T024, server side) — Testcontainers, same machine

| Path | p50 | p99 | n | Test |
| --- | --- | --- | --- | --- |
| Outbox commit → `realtime.fanout` (listener + 30 s backstop) | 6.7 ms | 17.0 ms | 20 | `OutboxWakeLatencyTests.A_row_committed_while_the_dispatcher_is_idle_…` |
| Same, after the listener backend was killed and reconnected | — | max 8.6 ms | 5 | `…A_dropped_listener_connection_reconnects_…` |
| Commit during the reconnect window | 118 ms | — | 1 | same |

The dominant cost (C1) is gone: commit-to-broker fell by roughly 30×. Browser end-to-end numbers
(Q1, Q2) still to be recorded here once the stack is up.

### Final (T055) — 2026-09-25, local, Testcontainers

| Suite | Result |
| --- | --- |
| Unit | pass (new: `DeliveryMetricsTests`, `RecordDeliveryLagTests`) |
| Architecture | 20/20 |
| Contract | 74/74 |
| Integration | 212/212 |
| Web (Vitest) | 499 pass / 6 fail — the same 6 fail on unmodified `main` (`chat-shell` ×2, `message-list` paging, `App` ×3); none caused by 002 |
| Coverage gate | Domain 93.3% ✓, web 98.4% ✓; **Application 41.6% ✗** (pre-existing; 002's new Application code is 93–100% covered); Infrastructure has no unit-coverage data by design |
| Dispatcher publish, 100-row batch | serial 149–169 ms → under the 60 ms budget with pipelined confirms |

Not yet run: browser e2e (`v2-delivery-timing`, `v5-idle-delivery`) and k6 — they need the full
Compose stack (API, web, nginx), which was not up during implementation.
