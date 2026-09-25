# Quickstart: Validating Instant Message Delivery

**Feature**: `002-realtime-message-delivery`

How to prove the feature works. Setup is the same as feature 001
(`specs/001-enterprise-chat-platform/quickstart.md`); only the validation steps are new.

## Prerequisites

```bash
docker compose -f deploy/docker-compose.yml up -d
docker compose -f deploy/docker-compose.observability.yml up -d   # needed for Q5
```

Seeded development roster, two users: `an.nguyen` and `binh.tran` (passwords equal usernames).

## Q0 — Baseline before the change (optional, for the before/after record)

```bash
cd tests/e2e && npx playwright test v2-delivery-timing
```

Expected on unmodified `main`: p95 near 1,000 ms, because of the fixed 1 s dispatcher sleep
(research R0, C1). Record the printed `delivery: p50 … p95 … p99 …` line.

## Q1 — Story 1: recipient sees the message immediately

```bash
cd tests/e2e && npx playwright test v2-delivery-timing
```

**Expected**: printed p95 ≤ 300 ms and p99 ≤ 500 ms over 50 samples (spec SC-001). The test fails
the run otherwise.

Manual check: two browsers side by side, type in one, and the message should appear in the other
with no perceptible pause.

## Q2 — Idle-then-send (spec FR-002, SC-006)

```bash
cd tests/e2e && npx playwright test v5-idle-delivery
```

The spec waits for the platform to be idle (no outbox rows for the idle period), then sends one
message and measures it. CI uses a shortened idle period (≥ 2 × the backstop poll interval and
beyond the Npgsql idle-connection prune time configured for the test stack). The nightly job uses
the full 10 minutes.

**Expected**: the first message after idle meets SC-001, in every trial.

## Q3 — Story 2: sender sees their own message immediately

Covered by the same Playwright run plus Vitest:

```bash
cd src/internalchat-web && npx vitest run conversation-view
```

**Expected**: the optimistic bubble renders synchronously on send (≤ 100 ms in the e2e
measurement); `sending → sent` without reorder or duplicate; a 403 produces `failed` with a reason
and is never shown to the recipient's browser.

## Q4 — Story 3: load, large groups, reconnect

```bash
k6 run tests/Load/messaging.js          # now also records delivery lag via the hub
cd tests/e2e && npx playwright test v3-groups
```

**Expected**:

- `delivery_lag_ms` p95 ≤ 300 ms at 100 msg/s sustained; 500-member group p95 ≤ 1 s during the
  1,000 msg/s burst (SC-003, SC-007).
- Disconnect a client for 2 minutes (`context.setOffline(true)`), reconnect: all missed messages
  appear once, in order, within 3 s, then live delivery resumes (SC-004).

## Q5 — Measurement and alerting (FR-011, FR-012, SC-009)

1. Open Grafana (`http://localhost:3000`) → dashboard **Message delivery**. Panels show p50/p95/p99
   for `chat_delivery_outbox_lag`, `chat_delivery_fanout_lag`, and `chat_delivery_client_lag`.
2. Degrade delivery on purpose:

   ```bash
   docker compose -f deploy/docker-compose.yml pause worker
   ```

   Send messages for 6 minutes. **Expected**: the **Delivery latency SLO** alert fires after 5
   minutes. `unpause worker`: all queued messages arrive once, in order; the alert resolves.
3. Confirm no metric series carries a conversation, message, or employee id:

   ```bash
   curl -s http://localhost:9090/api/v1/series?match[]=chat_delivery_fanout_lag_bucket | grep -c -E 'conversation|employee|message_id'
   ```

   **Expected**: `0`.

## Q6 — Degraded transport indicator (FR-010)

In a browser, open DevTools and block WebSocket connections to `/hubs/chat` (or run the e2e case
`v5-idle-delivery › long polling`). **Expected**: the "Limited connection" indicator appears;
messages still arrive with p95 ≤ 1 s (SC-005).

## Q7 — Guarantees unchanged (FR-008, SC-008, SC-010)

```bash
dotnet test tests/Integration --filter "Category=Messaging|Category=Resilience"
cd src/internalchat-web && npx vitest run message-store
```

**Expected**: all pass, including the new cases:

- A rolled-back send produces no `outbox_ready` notification and no delivery.
- The listener connection being killed (`pg_terminate_backend`) → delivery continues via the
  backstop poll, then returns to instant after reconnect.
- Concurrent fan-out: an edit or delete racing its own `MessageReceived` ends in the final state
  on every client (tombstone and latest-version rules).
- 10,000 messages under load with client disconnects and a Worker restart: zero lost, zero
  duplicated, zero out of order.

## Full suite

```bash
dotnet test tests/Unit && dotnet test tests/Architecture && dotnet test tests/Integration && dotnet test tests/Contract
cd src/internalchat-web && npx vitest run --coverage
cd tests/e2e && npx playwright test
```
