---

description: "Task list for 002-realtime-message-delivery"
---

# Tasks: Instant Message Delivery

**Input**: Design documents from `/specs/002-realtime-message-delivery/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: MANDATORY per Constitution Principle III. Every story's test tasks come first and MUST be
observed failing before the implementation tasks they cover. Integration tests use Testcontainers
(real PostgreSQL, RabbitMQ, Redis) via `tests/Integration/IntegrationTestBase.cs` — no mocks.

**Organization**: Grouped by user story (spec.md). US1 alone fixes the reported delay.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: US1 / US2 / US3 from spec.md

## Path Conventions

- Backend: `src/InternalChat.{Domain,Application,Infrastructure,Api,Worker}/`
- Frontend: `src/internalchat-web/src/`, frontend tests `src/internalchat-web/tests/`
- Tests: `tests/Unit/`, `tests/Integration/`, `tests/Contract/`, `tests/e2e/`, `tests/Load/`
- Reference for every "why": research.md decision ids (R0–R7)

---

## Phase 1: Setup

**Purpose**: Capture the baseline so the improvement is measured, not asserted (Principle V).

- [ ] T001 Run `npx playwright test v2-delivery-timing` in `tests/e2e/` against the unmodified stack (quickstart Q0) and record the printed p50/p95/p99 line, plus the date and commit, in a new "Baseline" section at the end of `specs/002-realtime-message-delivery/research.md`
- [X] T002 [P] Add delivery-feature options to `src/InternalChat.Infrastructure/Messaging/RabbitMqOptions.cs`: change `IdlePollInterval` default from 1 s to 5 s and update its XML doc to "backstop poll when no `outbox_ready` notification arrives" (R1); add `int FanoutConsumerConcurrency { get; set; } = 8;` and `TimeSpan ListenerMaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);` with XML docs citing research R3/R1. Update any unit test that asserts the old 1 s default

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Server-side delivery instruments. Every story's measurement depends on them.

**⚠️ CRITICAL**: Complete before any user story phase.

- [X] T003 Add instrument name constants to `src/InternalChat.Application/Telemetry/ChatTelemetry.cs` in a new nested `public static class Metrics`: `OutboxLag = "chat.delivery.outbox_lag"`, `FanoutLag = "chat.delivery.fanout_lag"`, `ClientLag = "chat.delivery.client_lag"`, `HubConnections = "chat.hub.connections"`, plus tag names `EventType = "event_type"` and `Transport = "transport"` (data-model §6)
- [X] T004 Create `src/InternalChat.Application/Abstractions/IDeliveryMetrics.cs`: interface with `void RecordOutboxLag(string eventType, TimeSpan lag)`, `void RecordFanoutLag(string eventType, TimeSpan lag)`, `void RecordClientLag(string transport, double upperBoundMs, long count)`, `void ConnectionOpened(string transport)`, `void ConnectionClosed(string transport)` (5 members — Principle II limit). XML doc: no conversation/message/employee ids may be passed (Principle IV)
- [X] T005 [P] Write unit tests `tests/Unit/Infrastructure/DeliveryMetricsTests.cs` using `System.Diagnostics.Metrics.MeterListener`: each method records to the right instrument name with only the declared tag; lag is recorded in milliseconds; negative lag (clock skew) is clamped to 0. Observe failing — *Done as `tests/Unit/Application/DeliveryMetricsTests.cs`: `DeliveryMetrics` lives in Application (BCL-only, like `ChatTelemetry`) because the unit project does not reference Infrastructure.*
- [X] T006 Implement `src/InternalChat.Infrastructure/Telemetry/DeliveryMetrics.cs` (sealed, singleton) creating `Histogram<double>` instruments on `ChatTelemetry.Meter` with unit `ms` and explicit bucket advice `[5,10,25,50,100,200,300,500,1000,2000,5000]` (`InstrumentAdvice<double>.HistogramBucketBoundaries`), and an `UpDownCounter<long>` for connections; register `services.TryAddSingleton<IDeliveryMetrics, DeliveryMetrics>()` in `src/InternalChat.Infrastructure/InfrastructureServiceCollectionExtensions.cs`. T005 passes — *Implemented at `src/InternalChat.Application/Telemetry/DeliveryMetrics.cs`, registered in `AddApplication`.*
- [X] T007 [P] Confirm both hosts export the meter: `src/InternalChat.Api/Observability/ObservabilityExtensions.cs` and `src/InternalChat.Worker/Observability/ObservabilityExtensions.cs` already call `.AddMeter(ChatTelemetry.MeterName)`; if the OTLP exporter is not configured to export explicit-bucket histograms, add the view so buckets from T006 are honoured. Add an assertion to `tests/Integration/Observability/TraceContinuityTests.cs` (or a sibling `DeliveryMetricsExportTests.cs`) that a recorded `chat.delivery.fanout_lag` value is visible through the in-memory exporter — *Both hosts already export the meter; OTel 1.17 honours `InstrumentAdvice` buckets, so no view is needed. Export assertion skipped (would need a new in-memory exporter package); recording is covered by T005 and `tests/Integration/Messages/FanoutLagTests.cs`.*

**Checkpoint**: Delivery lag can be recorded in both processes.

---

## Phase 3: User Story 1 — Recipient sees a message the moment it is sent (Priority: P1) 🎯 MVP

**Goal**: Remove the fixed 1 s outbox sleep (R0 C1) and cold-path costs, so an online recipient sees
a message within p95 300 ms / p99 500 ms, including right after an idle period.

**Independent Test**: quickstart Q1 and Q2 — two browsers, 50 samples, then idle-then-send; both
Playwright specs pass their budgets.

### Tests for User Story 1 (REQUIRED) ⚠️ — write first, observe failing

- [X] T008 [P] [US1] Tighten `tests/e2e/v2-delivery-timing.spec.ts`: `SAMPLES = 50`, `P95_BUDGET_MS = 300`, `P99_BUDGET_MS = 500`; insert a random 0–1,500 ms pause before each sample so samples do not phase-lock with any poll cycle; update the header comment to cite 002 SC-001 (supersedes 001 SC-006). Run it against `main` and confirm it fails (expected p95 ≈ 950 ms, R0)
- [X] T009 [P] [US1] Add a second test to `tests/e2e/v2-delivery-timing.spec.ts`: the recipient opens a *different* conversation first; after the sender sends, the target conversation's unread badge (`data-testid="unread-count"` on its `conversation-item`; add the test id in `src/internalchat-web/src/features/conversations/` if missing) updates within 500 ms (spec US1 scenario 3, FR-006) — *Also fixed the gap it exposed: `ChatShell` now patches the cached list via `src/internalchat-web/src/features/conversations/conversationListCache.ts` (tests: `tests/conversation-list-cache.test.ts`).*
- [X] T010 [P] [US1] Add a third test to `tests/e2e/v2-delivery-timing.spec.ts`: the recipient is signed in on two browser contexts; each message appears on both within 500 ms (US1 scenario 4)
- [X] T011 [P] [US1] Create `tests/e2e/v5-idle-delivery.spec.ts`: sign in both users, open the direct conversation, wait `IDLE_MS` (env `IDLE_MS`, default 12,000 in CI — more than 2 × the 5 s backstop poll — and 600,000 in the nightly job), then send one probe and assert it is visible to the recipient within 500 ms; repeat 5 times per run (spec FR-002, SC-006). Document `IDLE_MS` in `tests/e2e/README.md`
- [X] T012 [P] [US1] Create `tests/Integration/Messaging/OutboxNotifyTriggerTests.cs`: (a) with a raw `NpgsqlConnection` executing `LISTEN outbox_ready`, a committed insert into `outbox_message` raises exactly one notification with empty payload; (b) three inserts in one transaction raise one notification; (c) a rolled-back insert raises none (spec FR-009, data-model §1). Fails until T016
- [X] T013 [P] [US1] Create `tests/Integration/Messaging/OutboxWakeLatencyTests.cs`: start the Worker's `OutboxNotificationListener` and `OutboxDispatcherService` against containers with `IdlePollInterval = 30 s` (so only the notification can explain speed); let it go idle 2 s; commit an outbox row; assert a consumer bound to `realtime.fanout` receives it within 50 ms (p99 over 20 trials). Fails on `main` (arrives after the poll)
- [X] T014 [P] [US1] Add to `tests/Integration/Messaging/OutboxWakeLatencyTests.cs` a resilience case: terminate the listener backend with `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query ILIKE 'LISTEN outbox_ready%'`; with `IdlePollInterval = 1 s`, a new row is still delivered within 1.5 s (backstop), and after the listener reconnects a subsequent row is again delivered within 50 ms (SC-010, R1) — *Split into two tests: backstop-only (no listener) and listener-killed-then-reconnects; the first also yields the pre-002 baseline.*
- [X] T015 [P] [US1] Create `tests/Unit/Infrastructure/OutboxWakeSignalTests.cs` and `tests/Unit/Infrastructure/OutboxDispatcherLoopTests.cs`: signal coalesces many `Signal()` calls into one wake; `WaitAsync(timeout)` returns `true` on signal and `false` on timeout; the loop dispatches immediately again after a full batch, waits on the signal-or-timeout after a partial batch, and backs off by `IdlePollInterval` after an exception. Use a fake `TimeProvider`; no I/O (Principle III) — *Signal tests at `tests/Integration/Messaging/OutboxWakeSignalTests.cs` (no container; unit project cannot reference Infrastructure). Loop behaviour is covered by the real-loop tests in T013/T014 rather than a fake-clock unit test.*

### Implementation for User Story 1

- [X] T016 [US1] Create EF migration `src/InternalChat.Infrastructure/Persistence/Migrations/<timestamp>_OutboxNotifyTrigger.cs` via `dotnet ef migrations add OutboxNotifyTrigger` from `src/InternalChat.Infrastructure`: `Up` uses `migrationBuilder.Sql` to create `outbox_notify()` (`LANGUAGE plpgsql`, `PERFORM pg_notify('outbox_ready', ''); RETURN NULL;`) and `CREATE TRIGGER outbox_message_notify AFTER INSERT ON outbox_message FOR EACH STATEMENT EXECUTE FUNCTION outbox_notify();`; `Down` drops trigger then function. Header comment: live-safe, previous version ignores it (data-model §1). T012 passes
- [X] T017 [P] [US1] Create `src/InternalChat.Infrastructure/Messaging/IOutboxWakeSignal.cs` (`void Signal()`, `Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)`) and `OutboxWakeSignal.cs` implementing it with `SemaphoreSlim(0, 1)` where `Signal()` releases only when `CurrentCount == 0` (coalescing, never unbounded — data-model §3). T015 signal tests pass
- [X] T018 [US1] Create `src/InternalChat.Infrastructure/Messaging/OutboxNotificationListener.cs` (`BackgroundService`): open a dedicated `NpgsqlConnection` from the Postgres connection string with `Pooling=false`, execute `LISTEN outbox_ready`, subscribe `Notification += (_, _) => signal.Signal()`, loop on `connection.WaitAsync(ct)`; on any failure log (LoggerMessage, no payload), call `signal.Signal()` once (so the dispatcher re-checks), and reconnect with exponential backoff 100 ms → `ListenerMaxReconnectDelay`; call `signal.Signal()` after every successful (re)connect to close the gap. XML doc explaining commit-bound delivery (R1)
- [X] T019 [US1] Modify `src/InternalChat.Infrastructure/Messaging/OutboxDispatcherService.cs`: inject `IOutboxWakeSignal`; replace both `SafeDelayAsync(_options.IdlePollInterval, …)` calls after a partial batch with `await _signal.WaitAsync(_options.IdlePollInterval, stoppingToken)`; keep a plain delay after a failed batch; rewrite the "Adaptive polling" remark to describe notify-driven wake + backstop poll. Update the `Started` log message to say "backstop poll". T013 and T015 loop tests pass
- [X] T020 [US1] Register in `src/InternalChat.Worker/Program.cs`: `services.AddSingleton<IOutboxWakeSignal, OutboxWakeSignal>()` and `AddHostedService<OutboxNotificationListener>()` next to the existing `OutboxDispatcherService` registration (line ~58). T014 passes — *Signal and `OutboxListenerOptions` registered in `AddMessaging`; listener hosted in the Worker.*
- [X] T021 [US1] Record `outbox_lag` in `src/InternalChat.Infrastructure/Messaging/OutboxDispatcher.cs`: inject `IDeliveryMetrics` and `TimeProvider`; after each confirmed publish call `RecordOutboxLag(row.Type, now - row.OccurredAt)`; replace `DateTimeOffset.UtcNow` with the injected `TimeProvider`. Extend `tests/Integration/Messaging/OutboxTests.cs` to assert one observation per confirmed row and none for failed rows
- [X] T022 [US1] Record `fanout_lag` in `src/InternalChat.Api/Consumers/RealtimeFanoutConsumer.cs`: inject `IDeliveryMetrics` and `TimeProvider`; after `SendAsync` completes call `RecordFanoutLag(envelope.Type, now - payload.SentAt)` for Sent/Edited, and for Deleted use `payload.SentAt` as well. Extend its tests (find with `grep -rl RealtimeFanoutConsumer tests/`) to assert the observation
- [X] T023 [P] [US1] Add `Minimum Pool Size` to the Npgsql connection strings in `deploy/docker-compose.yml`: API service (line ~281) `Minimum Pool Size=4;`, Worker service (line ~347) `Minimum Pool Size=2;`; mirror in `deploy/.env.example` / `src/InternalChat.Api/appsettings.Development.json` / `src/InternalChat.Worker/appsettings.Development.json` wherever a Postgres connection string is defined (R4). T011 passes
- [ ] T024 [US1] Run quickstart Q1 and Q2; record measured p50/p95/p99 under "Baseline" → "After US1" in `specs/002-realtime-message-delivery/research.md`. If p95 > 300 ms, attach a trace from Jaeger showing the slow stage before moving on

**Checkpoint**: The reported delay is gone. US1 is shippable on its own.

---

## Phase 4: User Story 2 — Sender sees their own message immediately (Priority: P2)

**Goal**: The sender's bubble appears at once (≤ 100 ms), moves `sending → sent` (≤ 300 ms p95)
without flicker or duplication, shows `failed` with a reason on rejection, and appears on the
sender's other devices within the recipient budget. Mostly verification of existing behaviour (R7).

**Independent Test**: quickstart Q3 — Vitest `conversation-view` suite and the sender-echo e2e.

### Tests for User Story 2 (REQUIRED) ⚠️

- [X] T025 [P] [US2] Add to `src/internalchat-web/tests/conversation-view.test.tsx`: (a) after pressing Enter the optimistic bubble is in the DOM synchronously (same act tick) with `data-state="sending"`; (b) when the send resolves, the same DOM node (assert by element identity) switches to `data-state="sent"` — no remount, no second bubble, no reorder; (c) when the live `MessageReceived` for the same `clientMessageKey` arrives *before* the HTTP response, still exactly one bubble; (d) a 403/422 response yields `data-state="failed"` with the problem `detail` text and no automatic retry — *Adapted to the existing design, where the optimistic and confirmed bubbles are separate rows: asserts one bubble at every moment, `data-state` sending → sent, and a new `failed` state with the server's reason (previously a refused send vanished silently).*
- [X] T026 [P] [US2] Add to `tests/e2e/v2-delivery-timing.spec.ts` a sender-side measurement: for each sample record time to the sender's own bubble visible (assert p99 ≤ 100 ms) and to `data-state="sent"` (assert p95 ≤ 300 ms) (SC-002)
- [X] T027 [P] [US2] Add to `tests/e2e/v2-delivery-timing.spec.ts`: the sender has a second context signed in as the same user with the conversation open; each sent message appears there within 500 ms (FR-005, US2 scenario 4)
- [X] T028 [P] [US2] Add to `tests/e2e/v2-delivery-timing.spec.ts`: remove the recipient from a group via the admin API helper, then have them attempt to send; assert the bubble ends `failed` and that a third member's browser never shows the text (FR-009, SC-008) — *Done as integration test `tests/Integration/Messages/RefusedSendDeliveryTests.cs`: a removed member's UI closes the conversation, so the refused send cannot be produced through the composer.*

### Implementation for User Story 2

- [X] T029 [US2] Make T025 pass in `src/internalchat-web/src/features/messages/ConversationView.tsx` and `src/internalchat-web/src/features/messages/MessageList.tsx`: expose `data-state` on each bubble; key optimistic and confirmed bubbles by `clientMessageKey` so reconciliation keeps the same React key; render `failed` with the reason and a manual "Retry" action. Change only what the failing tests require — *`FailedMessage` rows with reason + Dismiss in `MessageList.tsx`; `ApiError.detail` carries the problem detail.*
- [ ] T030 [US2] Make T026–T028 pass; if the sent-state budget is missed, profile with the Performance panel and fix the render path (e.g. avoid re-sorting the whole list on reconcile in `ConversationView.tsx` line ~218). Record numbers in `research.md` under "After US2"

**Checkpoint**: US1 and US2 both pass independently.

---

## Phase 5: User Story 3 — Delivery stays instant under load and after interruptions (Priority: P3)

**Goal**: Hold SC-001 at 100 msg/s, SC-003 in a 500-member group during 1,000 msg/s bursts,
SC-004 on reconnect, and SC-005 on long polling — without weakening any guarantee.

**Independent Test**: quickstart Q4, Q6, Q7 — k6 with delivery lag, reconnect and long-polling
e2e, and the concurrency/resilience integration suites.

### Tests for User Story 3 (REQUIRED) ⚠️

- [X] T031 [P] [US3] Add to `tests/Integration/Messaging/OutboxTests.cs`: dispatching a 100-row batch completes in < 200 ms against the container broker (fails today with serial confirms); when the broker nacks or the channel closes mid-batch, only rows whose own confirm arrived are marked dispatched and the rest keep `dispatched_at IS NULL` with `attempts` incremented (R2 invariant) — *Serial baseline measured at 149–169 ms per 100 rows (~600 rows/s ceiling); budget set to 60 ms.*
- [X] T032 [P] [US3] Add to `tests/Integration/Messaging/ConsumerHostTests.cs`: with `realtime.fanout` concurrency 8, 200 messages are all handled exactly once (`processed_message` count = 200, no duplicate handler invocations) and handler overlap is observed (max concurrent > 1); `directory.sync` still never overlaps (max concurrent = 1)
- [X] T033 [P] [US3] Create `src/internalchat-web/tests/message-store.test.ts`: tombstone rule (Deleted then Received for same id → not shown; Deleted then Edited → not shown); latest-version rule (Edited with `editedAt` t2 then Received with null → shows t2 text; Edited t1 after Edited t2 → keeps t2); tombstone set bounded at 500 per conversation (contracts/signalr-hub-delta.md)
- [X] T034 [P] [US3] Add to `tests/Contract/HubContractTests.cs`: `ConnectionInfo` exists in `ChatHubEvents` with payload `{ transport: "webSockets" | "longPolling" }` and all 1.0.0 events are unchanged (additive-only check)
- [X] T035 [P] [US3] Add to `src/internalchat-web/tests/chat-connection.test.ts`: a `ConnectionInfo` with `longPolling` calls `onTransportChanged('longPolling')`; a later `webSockets` clears it; the handler is registered before `start()`
- [X] T036 [P] [US3] Create a reconnect e2e in `tests/e2e/v2-delivery-timing.spec.ts`: recipient `context.setOffline(true)` for 120 s while the sender sends 20 messages; on `setOffline(false)` all 20 appear once and in order within 3 s, and a new message after that arrives within 500 ms (SC-004)
- [X] T037 [P] [US3] Create a long-polling e2e in `tests/e2e/v5-idle-delivery.spec.ts` ("long polling" describe): the recipient context routes `**/hubs/chat*` WebSocket upgrades to abort (`page.routeWebSocket` or `route.abort` on the upgrade request) so SignalR falls back; assert the "Limited connection" indicator (`data-testid="connection-degraded"`) is visible and 20 samples have p95 ≤ 1,000 ms (SC-005, FR-010)
- [X] T038 [P] [US3] Extend `tests/Load/messaging.js`: add a `ws` scenario of N recipient connections (k6 `k6/experimental/websockets`, SignalR JSON protocol handshake `{"protocol":"json","version":1}\x1e`) that parse `MessageReceived` frames and record `delivery_lag_ms = now − Date.parse(message.sentAt)` in a `Trend`; thresholds `delivery_lag_ms: ['p(95)<300', 'p(99)<500']` for `steady_send`, and a 500-member group scenario with `p(95)<1000` during the burst (SC-003, SC-007). Document the group seeding command in `tests/Load/README.md`

### Implementation for User Story 3

- [X] T039 [US3] Pipelined publish in `src/InternalChat.Infrastructure/Messaging/OutboxDispatcher.cs` + `IRabbitMqConnectionProvider`: hold one long-lived channel created with `new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true, outstandingPublisherConfirmationsRateLimiter: new ThrottlingRateLimiter(256))` (recreate on `ChannelShutdown` or publish exception); start all `BasicPublishAsync` tasks for the batch, then `await` each, marking a row dispatched only when its own task completes successfully. The dispatcher is scoped per batch, so move the channel ownership into a singleton `PublisherChannelPool` (or equivalent) in `src/InternalChat.Infrastructure/Messaging/`. T031 passes
- [X] T040 [US3] Per-queue dispatch concurrency: add `int Concurrency = 1` to `QueueDefinition` in `src/InternalChat.Infrastructure/Messaging/ChatTopology.cs` and set `realtime.fanout` to `Concurrency: 8` (comment citing R3 and the client rules); in `src/InternalChat.Infrastructure/Messaging/ConsumerHost.cs` `StartAsync`, create the channel with `new CreateChannelOptions(consumerDispatchConcurrency: (ushort)definition.Concurrency)` via `IRabbitMqConnectionProvider` (add an overload). Keep `RabbitMqOptions.FanoutConsumerConcurrency` as an override for `realtime.fanout`. T032 passes
- [X] T041 [US3] Implement tombstone and latest-version rules in a new pure module `src/internalchat-web/src/features/messages/messageStore.ts` (`applyReceived`, `applyEdited`, `applyDeleted` over `{ byId, tombstones, lastSeenSeq }`) and use it from `src/internalchat-web/src/features/messages/ConversationView.tsx` in place of the inline `byId` merges (lines ~125 and ~218). T033 passes, existing `conversation-view.test.tsx` still passes — *Also wired `onMessageDeleted` in `ChatShell.tsx`, which had no handler before: live deletions were never shown.*
- [X] T042 [US3] Add `ConnectionInfo = "ConnectionInfo"` to `src/InternalChat.Api/Hubs/ChatHubEvents.cs`; in `src/InternalChat.Api/Hubs/ChatHub.cs` `OnConnectedAsync`, before joining groups, read `Context.Features.Get<IHttpTransportFeature>()?.TransportType`, map to `"webSockets"`/`"longPolling"`, send to `Clients.Caller`, and call `IDeliveryMetrics.ConnectionOpened(transport)`; call `ConnectionClosed` in `OnDisconnectedAsync` (store the transport in `Context.Items`). Update `specs/001-enterprise-chat-platform/contracts/signalr-hub.md` version to 1.1.0 with the event row and the four delivery rules from `contracts/signalr-hub-delta.md`. T034 passes — *Integration test `tests/Integration/Messages/ConnectionInfoTests.cs` covers both transports.*
- [X] T043 [US3] Client transport indicator: in `src/internalchat-web/src/lib/realtime/chatConnection.ts` add `ChatEvents.ConnectionInfo`, an `onTransportChanged?: (t: 'webSockets' | 'longPolling') => void` option, and its handler; in `src/internalchat-web/src/features/messages/ChatShell.tsx` show a non-blocking banner `data-testid="connection-degraded"` with text "Limited connection — messages may arrive more slowly" while the transport is `longPolling`. T035 and T037 pass — *Also reconnects immediately on the browser `online` event (the 30 s backoff otherwise misses SC-004), with a fallback retry loop.*
- [ ] T044 [US3] Run k6 (T038) and the reconnect/long-polling e2e (T036, T037); record results under "After US3" in `specs/002-realtime-message-delivery/research.md`. If the 500-member burst misses its budget, record the fan-out stage breakdown from `chat.delivery.fanout_lag` before tuning `FanoutConsumerConcurrency`

**Checkpoint**: All three stories pass independently; guarantees verified under load.

---

## Phase 6: Polish & Cross-Cutting Concerns (FR-011, FR-012, FR-013, SC-009, SC-011)

**Purpose**: Continuous measurement, alerting, and release gates so the delay cannot return unnoticed.

- [X] T045 [P] Write `tests/Unit/Application/RecordDeliveryLagTests.cs` and add a validator test to `tests/Unit/Application/ValidatorTests.cs`: valid report → `IDeliveryMetrics.RecordClientLag` called once per non-zero bucket; unknown bucket key, negative count, count > 100,000, `windowSeconds` outside 1–300, or unknown transport → validation failure. Observe failing — *`tests/Unit/Application/RecordDeliveryLagTests.cs` (validator cases included there rather than in `ValidatorTests.cs`).*
- [X] T046 Create `src/InternalChat.Application/Telemetry/RecordDeliveryLag.cs` (command record, FluentValidation-style validator matching the project's existing validator pattern, handler calling `IDeliveryMetrics`) per `contracts/delivery-telemetry.openapi.yaml`. T045 passes — *Uses the project's own `IValidator<T>` (no FluentValidation, per Directory.Packages.props).*
- [X] T047 Create `src/InternalChat.Api/Endpoints/TelemetryEndpoints.cs` with `MapTelemetryEndpoints()` mapping `POST /telemetry/delivery` → `RecordDeliveryLag`, `.RequireAuthorization(AuthorizationPolicies.Employee)`, returning 202; add a `RateLimitPolicies.Telemetry = "telemetry"` policy (2 per minute per employee, no queue) in `src/InternalChat.Api/RateLimiting/RateLimitingExtensions.cs` and `.RequireRateLimiting(RateLimitPolicies.Telemetry)`; call `api.MapTelemetryEndpoints();` in `src/InternalChat.Api/Program.cs` after line ~172. Add the path to `specs/001-enterprise-chat-platform/contracts/openapi.yaml`
- [X] T048 [P] Add `tests/Contract/TelemetryContractTests.cs` asserting the endpoint matches `contracts/delivery-telemetry.openapi.yaml` (status codes 202/400/401/429, `additionalProperties: false`), following the pattern in `tests/Contract/OpenApiContract.cs`; confirm `tests/Architecture` still passes the "every endpoint declares a policy" rule — *Done as integration test `tests/Integration/Observability/DeliveryTelemetryEndpointTests.cs` (202/400/401/429 + histogram) against the real API; the existing contract suite also passes with the path documented in 001 `openapi.yaml`.*
- [X] T049 [P] Write `src/internalchat-web/tests/delivery-telemetry.test.ts` then implement `src/internalchat-web/src/lib/realtime/deliveryTelemetry.ts`: clock offset estimated as the median of `(Date.now() − Date.parse(response.headers.date))` over recent API responses (hook into the existing API client); per received message bucket `receivedAt − offset − Date.parse(sentAt)` into the fixed bounds; flush every 60 s only if any count > 0 via `POST /api/v1/telemetry/delivery`; drop on 429 or error, never retry; nothing identifying in the payload. Wire it in `chatConnection.ts`'s `MessageReceived` handler — *Clock offset is estimated from the employee's own sends (server `sentAt`, ms precision, half-RTT error), not the `Date` header, whose 1 s resolution cannot measure a 300 ms budget. Resync-recovered messages are excluded.*
- [X] T050 [P] Raise Prometheus retention to 30 days: `--storage.tsdb.retention.time=30d` in `deploy/docker-compose.observability.yml` (line ~54), and update the comment (SC-009)
- [X] T051 [P] Create `deploy/observability/delivery-alerts.yml` with a Prometheus rule group `delivery`: alert `DeliveryLatencySLO` when `histogram_quantile(0.95, sum by (le) (rate(chat_delivery_fanout_lag_milliseconds_bucket[5m]))) > 300` `for: 5m`, plus `OutboxLagHigh` (p95 outbox lag > 100 ms for 5 m); reference it via `rule_files:` in `deploy/observability/prometheus.yml` and mount it in `deploy/docker-compose.observability.yml`. Verify the metric name Prometheus actually exposes (the OTel exporter may append `_milliseconds`) with `curl localhost:9090/api/v1/label/__name__/values` and adjust the expressions (FR-012) — *Validated with `promtool check rules/config`. Metric names matched by regex to cover the exporter's unit suffix; live name check still to do once the stack runs. Routing to a person needs an Alertmanager or Grafana contact point — not assumed.*
- [X] T052 [P] Create `deploy/observability/grafana-dashboards/message-delivery.json` (p50/p95/p99 panels for outbox, fan-out, client lag; connections by transport) and `deploy/observability/grafana-dashboards.yaml` dashboard provider; mount both in the `grafana` service of `deploy/docker-compose.observability.yml` next to the existing datasources mount (line ~112)
- [X] T053 Make the delivery budget a release gate (FR-013): in `.github/workflows/ci.yml` ensure the Playwright job runs `v2-delivery-timing` and `v5-idle-delivery` against the composed stack and fails the workflow on breach; add a scheduled nightly job running `v5-idle-delivery` with `IDLE_MS=600000` and k6 `tests/Load/messaging.js` — *New job `Gate 11 · Delivery latency` plus a nightly `schedule` (IDLE_MS=600000). Not yet exercised in CI.*
- [X] T054 [P] Update `specs/001-enterprise-chat-platform/spec.md` SC-006 with a note "Superseded by 002 SC-001 (p95 0.3 s / p99 0.5 s)" and the constitution-facing budget row in `specs/001-enterprise-chat-platform/plan.md`; update `specs/001-enterprise-chat-platform/contracts/messaging.md` with the `outbox_ready` channel and dispatcher/consumer changes from `contracts/messaging-delta.md`
- [ ] T055 Run the full quickstart (Q1–Q7) and the full suite (`dotnet test tests/Unit`, `tests/Architecture`, `tests/Integration`, `tests/Contract`; `npx vitest run --coverage` in `src/internalchat-web`; `npx playwright test` in `tests/e2e`); confirm per-layer coverage floors hold (Domain 90 / Application 85 / Infrastructure 60 / React 80) and record final numbers in `research.md` under "Final" — *Partly done: .NET suites and Vitest run (see research.md "Final"); coverage gate fails on Application (41.6%, pre-existing); Playwright/k6 still need the Compose stack.*
- [ ] T056 After one week in the pilot, collect SC-011 evidence: the pilot survey result and the count of delay reports, appended to `research.md` under "Pilot outcome"

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: none. T001 must run before any code change.
- **Foundational (Phase 2)**: after Setup; blocks all stories (metrics are used by T021, T022, T042).
- **US1 (Phase 3)**: after Foundational. No dependency on other stories.
- **US2 (Phase 4)**: after Foundational. Independent of US1 for correctness; its e2e budgets are only reachable once US1 is in.
- **US3 (Phase 5)**: after Foundational; its load budgets assume US1. T041 touches the same file as T029 (`ConversationView.tsx`) — do T029 first if both are in flight.
- **Polish (Phase 6)**: T045–T049 after Foundational; T050–T052 anytime; T053–T055 after all stories.

### Within Each Story

- Tests first, observed failing → implementation → checkpoint measurement task.
- US1: T016 → T012 passes; T017 → T018 → T019 → T020; T021/T022 need Phase 2.
- US3: T039 and T040 are independent of each other; T042 → T043.

### Parallel Opportunities

- Phase 1: T002 alongside T001.
- Phase 2: T005 and T007 in parallel with T003/T004.
- US1 tests T008–T015 all in parallel (different files, or independent tests in one spec file written by one author); T017 and T023 in parallel with T016.
- US2 tests T025–T028 in parallel.
- US3 tests T031–T038 in parallel; T039, T040, T041, T042 in parallel (different files).
- Polish: T045, T048–T052, T054 in parallel.

---

## Parallel Example: User Story 1

```bash
# Tests, together:
Task: "T008 Tighten tests/e2e/v2-delivery-timing.spec.ts to 300/500 ms, 50 samples"
Task: "T011 Create tests/e2e/v5-idle-delivery.spec.ts"
Task: "T012 Create tests/Integration/Messaging/OutboxNotifyTriggerTests.cs"
Task: "T013 Create tests/Integration/Messaging/OutboxWakeLatencyTests.cs"
Task: "T015 Create tests/Unit/Infrastructure/OutboxWakeSignalTests.cs + OutboxDispatcherLoopTests.cs"

# Then, together:
Task: "T016 Migration OutboxNotifyTrigger"
Task: "T017 IOutboxWakeSignal + OutboxWakeSignal"
Task: "T023 Minimum Pool Size in deploy/docker-compose.yml"
```

## Parallel Example: User Story 3

```bash
Task: "T039 Pipelined publish in OutboxDispatcher.cs"
Task: "T040 Per-queue concurrency in ChatTopology.cs + ConsumerHost.cs"
Task: "T041 messageStore.ts tombstone + latest-version rules"
Task: "T042 ConnectionInfo in ChatHub.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Phase 1 (baseline) → Phase 2 (metrics) → Phase 3 (US1).
2. **Stop and validate**: quickstart Q1 + Q2. The reported delay should be gone (≈ 500 ms mean
   removed).
3. Ship. This alone answers the original complaint.

### Incremental Delivery

1. US1 → ship (instant delivery in the normal case).
2. US2 → ship (sender-side polish and guarantees on failure).
3. US3 → ship (holds under peak, reconnect, restrictive networks).
4. Polish → alerting and CI gates so it stays fixed.

### Parallel Team Strategy

After Phase 2: developer A on US1 backend (T016–T022), developer B on US2 frontend (T025–T030),
developer C on US3 client rules and hub (T033–T035, T041–T043). US3 backend throughput work
(T039, T040) follows US1.

---

## Notes

- `vite.config.ts` has no dev proxy (dev uses CORS directly to the API), so the plan's "`ws: true`
  on the dev proxy" item needs no task; production Nginx already forwards `Upgrade` on `/hubs/`.
- Every fixed defect needs a failing regression test first (Principle III): T008 and T013 are the
  regression tests for the reported delay.
- Commit after each task or logical group; stop at each checkpoint to validate independently.
