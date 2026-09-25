# Implementation Plan: Instant Message Delivery

**Branch**: `002-realtime-message-delivery` (spec directory; the repository is currently on `main`)
| **Date**: 2026-09-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-realtime-message-delivery/spec.md`

## Summary

Employees see a lag between a colleague pressing send and the message appearing. Reading the
delivery path shows why: the Worker's outbox dispatcher sleeps a **fixed 1 second** whenever the
outbox is empty, so every message on a normally loaded platform waits 0–1 s (mean ≈ 500 ms) before
it is even published to RabbitMQ (research R0). Smaller costs stack on top: a new AMQP channel per
batch, serial publisher confirms, a serial fan-out consumer, and cold database connections after
idle.

The fix keeps the architecture intact — transactional outbox, RabbitMQ, Redis-backed SignalR — and
removes the waiting:

1. **Wake on commit** — a PostgreSQL `NOTIFY` fired by an outbox trigger wakes the dispatcher the
   moment a send commits; the timed poll stays as a 5 s safety net (R1).
2. **Faster publish** — one long-lived confirming channel, pipelined confirms (R2).
3. **Parallel fan-out** — `realtime.fanout` handles 8 events concurrently, with two client rules
   (tombstone, latest-version) that make arrival order irrelevant (R3).
4. **Warm paths** — minimum Npgsql pool sizes so the first message after idle is not slower (R4).
5. **Visible degradation** — the hub tells the client its transport; long polling shows an
   indicator (R5).
6. **Measure and gate** — stage histograms, client-observed lag, a Grafana alert, and tightened
   Playwright/k6 gates so the delay cannot quietly return (R6).

## Technical Context

**Language/Version**: C# 14 on .NET 10 (LTS); TypeScript 5.x on React 19 — unchanged from 001

**Primary Dependencies**: ASP.NET Core 10 (Minimal APIs + SignalR, Redis backplane), EF Core 10 +
Npgsql (LISTEN/NOTIFY), RabbitMQ.Client 7 (publisher-confirmation tracking), OpenTelemetry .NET,
`@microsoft/signalr`. **No new dependencies.**

**Storage**: PostgreSQL 17 — one trigger + function on `outbox_message`; no schema change to data
tables. Redis 7 unchanged. Prometheus retention 15 d → 30 d.

**Testing**: xUnit + NSubstitute (unit); Testcontainers PostgreSQL + RabbitMQ + Redis (integration:
listener, trigger, pipelined confirms, concurrent fan-out); Vitest + RTL (client store rules,
indicator); Playwright (end-to-end latency, idle-then-send, reconnect); k6 (load with delivery lag).

**Target Platform**: Linux containers under Docker Compose, single application host — unchanged.

**Project Type**: Web application (existing Clean Architecture backend + React SPA).

**Performance Goals**: end-to-end send→visible p95 300 ms / p99 500 ms; commit→hub-send p95 150 ms /
p99 300 ms; sender echo ≤ 100 ms p99; 500-member group p95 ≤ 1 s; reconnect catch-up ≤ 3 s.

**Constraints**: every feature 001 guarantee held (exactly-once in effect, per-conversation order
by `seq`, deny-by-default membership isolation, no message bodies in broker or metrics); no new
runtime component; migration live-safe.

**Scale/Scope**: 7,000 concurrent connections, 100 msg/s sustained, 1,000 msg/s bursts (001
SC-011/012). Touches ~10 backend files, ~4 frontend files, 1 migration, compose/observability config,
and tests.

## Constitution Check

*Source: `.specify/memory/constitution.md` v1.2.0.*

| # | Gate | Status | Notes |
| --- | --- | --- | --- |
| I | **Clean Architecture** | PASS | Listener, dispatcher, and consumer concurrency live in Infrastructure; hub `ConnectionInfo` and telemetry endpoint in Presentation; the endpoint delegates to an Application use case `RecordDeliveryLag` that writes through an Application-owned `IDeliveryMetrics` interface. No Domain change. |
| II | **SOLID** | PASS | New single-purpose types: `OutboxNotificationListener` (wake signal), `DeliveryMetrics` (instruments). `OutboxDispatcherService` gains a dependency on a narrow `IOutboxWakeSignal` (2 members), not on Npgsql. |
| III | **Test-First** | PASS | Each change has a failing test first: dispatcher wakes within 50 ms of commit (integration, fails today at ~1 s); rollback produces no wake; listener loss falls back to poll; pipelined confirms mark only confirmed rows; concurrent fan-out end-state; client tombstone/version rules; e2e p95 ≤ 300 ms and idle-then-send. Floors unaffected; new code is covered at its layer's floor. |
| IV | **Security by Default** | PASS | Telemetry endpoint has an explicit authenticated policy, strict schema validation, and a rate limit; payload contains no identifiers or content. `ConnectionInfo` goes to the caller only. Metrics carry no ids. Fan-out authorization unchanged (group membership). No auth/crypto/isolation/retention change → no security sign-off required, but reviewer to confirm concurrency does not bypass group membership. |
| V | **Performance Budgets** | PASS | Budget declared below; measured by Playwright, k6, and production histograms; gates fail CI. Stateless: the wake signal is per-process and advisory; any number of Worker replicas share the outbox via `SKIP LOCKED`. |
| VI | **Messaging Contracts** | PASS | Outbox atomicity preserved and strengthened (NOTIFY is commit-bound). Consumers stay idempotent; concurrency does not change the `processed_message` guard. Contracts unchanged (all `.v1`); hub contract bumped additively to 1.1.0. DLQ/retry unchanged. Ordering: `seq`-based, explicitly documented. |
| VII | **Data Authority & Cache** | PASS | PostgreSQL remains authority; nothing new cached. Migration adds a function and trigger only — forward-only, live-safe, the previous version ignores it. |
| VIII | **Zero-Cost & Self-Hosted** | PASS | No new component or dependency; LISTEN/NOTIFY is core PostgreSQL; alerting uses the already-deployed Grafana/Prometheus. |
| — | **Stack** | PASS | Mandated stack only. |
| — | **Docker-First** | PASS | Only config changes to existing compose files (pool sizes, Prometheus retention, alert rule file); `docker compose up` from clean checkout unchanged. |

**Result: PASS**, no deviations; Complexity Tracking is empty.

**Declared performance budget for this feature**

| Operation | p95 | p99 | Measured by |
| --- | --- | --- | --- |
| Send pressed → visible to online recipient (end-to-end) | 300 ms | 500 ms | Playwright `v2-delivery-timing` (50 samples, CI gate); `chat.delivery.client_lag` in production |
| First message after ≥ 10 min idle → visible | 300 ms | 500 ms | Playwright `v5-idle-delivery` (CI shortened idle; nightly 10 min) |
| Outbox commit → broker confirm | 25 ms | 100 ms | `chat.delivery.outbox_lag` |
| Message commit → hub `SendAsync` done | 150 ms | 300 ms | `chat.delivery.fanout_lag`; alert when p95 > 300 ms for 5 min |
| Send pressed → own optimistic bubble | — | 100 ms | Playwright |
| 500-member group, online members receive | 1 s | 2 s | k6 + hub listeners |
| Reconnect after ≤ 5 min → caught up | 3 s | 5 s | Playwright offline/online |
| Long-polling client delivery | 1 s | 2 s | Playwright with WebSockets blocked |
| Send accept (unchanged, 001) | 150 ms | 300 ms | k6 `messaging.js` — must not regress from the trigger |

This tightens the constitution's end-to-end row (500 ms / 1 s) for this path; the constitution's
figure remains the floor, not the target.

**Declared security surface for this feature**

- **New external input**: `POST /api/v1/telemetry/delivery` — authenticated employee, rate limited
  (2/min), strict schema (`additionalProperties: false`, bounded integers), no identifiers.
- **New hub event**: `ConnectionInfo` to the caller only; transport name, nothing else.
- **Authorization changes**: none. Fan-out still targets `conv:{id}` groups whose membership is
  maintained by `membership.fanout`; concurrency changes timing, not recipients.
- **Data classes**: none new. PostgreSQL `NOTIFY` payload is empty.
- **Audit events**: none new (performance tuning is not an audited action). Alert firings are
  recorded by Grafana.

## Project Structure

### Documentation (this feature)

```text
specs/002-realtime-message-delivery/
├── plan.md                               # This file
├── research.md                           # R0 as-built analysis + R1–R7 decisions
├── data-model.md                         # Trigger, lifecycle timing, client store rules, metrics
├── quickstart.md                         # Q0–Q7 validation steps
├── contracts/
│   ├── signalr-hub-delta.md              # Hub 1.0.0 → 1.1.0 (ConnectionInfo, ordering rules)
│   ├── messaging-delta.md                # outbox_ready channel, dispatcher & consumer behaviour
│   └── delivery-telemetry.openapi.yaml   # POST /api/v1/telemetry/delivery
├── checklists/requirements.md
└── tasks.md                              # /speckit-tasks (not created here)
```

### Source Code (repository root)

```text
src/
├── InternalChat.Application/
│   ├── Abstractions/IDeliveryMetrics.cs                 # NEW — record lag observations
│   ├── Telemetry/RecordDeliveryLag.cs                   # NEW — use case for client reports
│   └── Telemetry/ChatTelemetry.cs                       # + instrument names
├── InternalChat.Infrastructure/
│   ├── Messaging/
│   │   ├── IOutboxWakeSignal.cs                         # NEW — Wait/Signal, coalescing
│   │   ├── OutboxNotificationListener.cs                # NEW — BackgroundService, LISTEN outbox_ready
│   │   ├── OutboxDispatcherService.cs                   # wait on signal OR backstop poll
│   │   ├── OutboxDispatcher.cs                          # long-lived channel, pipelined confirms, outbox_lag
│   │   ├── ConsumerHost.cs                              # per-queue dispatch concurrency
│   │   ├── ChatTopology.cs                              # QueueDefinition + Concurrency (realtime.fanout = 8)
│   │   └── RabbitMqOptions.cs                           # IdlePollInterval 5 s
│   ├── Telemetry/DeliveryMetrics.cs                     # NEW — Meter-backed IDeliveryMetrics
│   └── Persistence/Migrations/<ts>_OutboxNotifyTrigger.cs   # NEW
├── InternalChat.Api/
│   ├── Hubs/ChatHub.cs                                  # send ConnectionInfo in OnConnectedAsync
│   ├── Hubs/ChatHubEvents.cs                            # + ConnectionInfo
│   ├── Consumers/RealtimeFanoutConsumer.cs              # record fanout_lag
│   └── Endpoints/TelemetryEndpoints.cs                  # NEW — POST /api/v1/telemetry/delivery
├── InternalChat.Worker/Program.cs                       # register listener + signal
└── internalchat-web/
    ├── vite.config.ts                                   # /hubs proxy ws: true (dev)
    └── src/
        ├── lib/realtime/chatConnection.ts               # ConnectionInfo handler, lag sampling
        ├── lib/realtime/deliveryTelemetry.ts            # NEW — clock offset, buckets, 60 s report
        └── features/messages/ConversationView.tsx       # tombstone + latest-version rules, indicator

deploy/
├── docker-compose.yml / .env.example                    # Npgsql Minimum Pool Size
├── docker-compose.observability.yml                     # Prometheus 30d, rule file mount
└── observability/
    ├── delivery-alerts.yml                              # NEW — p95 > 300 ms for 5 m
    └── grafana-dashboards/message-delivery.json         # NEW

tests/
├── Unit/                     # dispatcher loop (signal vs poll), signal coalescing, RecordDeliveryLag validation
├── Integration/Messaging/    # trigger fires on commit only; wake < 50 ms; listener loss; pipelined confirms; concurrent fan-out
├── Contract/                 # hub 1.1.0 additive; telemetry OpenAPI
├── e2e/                      # v2-delivery-timing (tightened, 50 samples), v5-idle-delivery (NEW)
└── Load/messaging.js         # + hub listeners, delivery_lag_ms threshold
```

**Structure Decision**: The existing Clean Architecture layout from feature 001 is kept exactly;
this feature modifies files in place and adds a handful of single-purpose types in the layers named
above. No new project, host, or container.

## Implementation Order (for /speckit-tasks)

1. **Reproduce first** (Principle III): tighten `v2-delivery-timing` to 300/500 ms and add an
   integration test "dispatcher publishes within 50 ms of commit" — both fail on `main`.
2. **R1** trigger migration + `OutboxNotificationListener` + signal-driven loop → the reported
   delay is gone. *Shippable on its own (Story 1 MVP).*
3. **R4** pool sizes + `v5-idle-delivery`.
4. **R6** server histograms, dashboard, alert — so the rest is measured, not guessed.
5. **R2** pipelined publish; **R3** fan-out concurrency + client rules → Story 3 under load.
6. **R5** `ConnectionInfo` + indicator; **R6** client telemetry endpoint.
7. Record that 001 SC-006 is superseded by 002 SC-001 in the 001 spec.

## Complexity Tracking

No constitution violations. Nothing to justify.
