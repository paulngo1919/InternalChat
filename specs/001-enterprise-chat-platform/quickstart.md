# Quickstart & Validation Guide

**Feature**: Enterprise Internal Chat Platform | **Plan**: [plan.md](./plan.md)

This is the run-and-verify guide. Implementation detail belongs in `tasks.md`, not here.

Constitution Principle VIII requires onboarding to be exactly three steps with no external account
and no manual configuration. If you find yourself doing a fourth step, that is a defect to automate,
not to document.

## Prerequisites

| Requirement | Notes |
| --- | --- |
| Docker Engine 27+ with Compose v2 | The only hard requirement |
| .NET 10 SDK | Only for running tests and building outside containers |
| Node 22+ | Only for frontend development outside containers |
| 16 GB RAM, 40 GB free disk | Full stack including PostgreSQL, Keycloak, MinIO, observability |

No cloud account, no VPN, no licence key. After the first image pull the stack runs fully offline.

## Start

```bash
git clone <repo> && cd InternalChat
cp deploy/.env.example deploy/.env
docker compose -f deploy/docker-compose.yml up -d
```

Seeded on first run: a Keycloak realm with 20 development employees, a database schema, three
conversations with history, and sample attachments — so you can sign in and send a message
immediately rather than configuring your way to a working system.

| Service | URL | Credentials |
| --- | --- | --- |
| Web app | <http://localhost:8080> | `alice` / `alice` (see `deploy/keycloak/README`) |
| API + OpenAPI | <http://localhost:8081/swagger> | Bearer token from Keycloak |
| Keycloak | <http://localhost:8082> | from `.env` |
| RabbitMQ management | <http://localhost:15672> | from `.env` |
| MinIO console | <http://localhost:9001> | from `.env` |
| Grafana | <http://localhost:3000> | from `.env` |

Meetings (US8, US9) need the media host, which is deliberately separate:

```bash
docker compose -f deploy/docker-compose.media.yml up -d
```

**Verify the constitution's degradation requirement**: with the media host stopped, the platform
must stay fully usable and meetings must report unavailable. If messaging breaks when LiveKit is
down, that is a constitution violation, not a minor bug.

## Verification scenarios

Each maps to a user story and its success criteria. Run these before claiming a story is done.

### V1 — Sign-in and revocation (US1 · SC-018)

1. Sign in as `alice`. You reach the conversation list without creating a chat password.
2. Request a conversation you are not a member of via the API. Expect `403` with a Problem Details
   body, indistinguishable from `404`.
3. Deactivate `alice` in Keycloak while her browser session is open.
4. Within 5 minutes her open session stops working — including the already-established WebSocket.

> Step 4 is the one that fails in most implementations. An open connection that outlives its token
> is the default behaviour, not the exception.

### V2 — Exactly-once messaging (US2 · SC-006, SC-022)

1. Sign in as `alice` and `bob` in two browsers. Send from `alice`; it appears for `bob` with no
   refresh, within 500 ms.
2. Send the same `clientMessageKey` twice. Expect `201` then `200`, and exactly one message in the
   conversation.
3. Kill `bob`'s network, send three messages, restore it. All three appear once, in order.
4. Set `bob`'s device clock forward one hour and send. Ordering is unaffected — `seq` is the
   authority, not the clock.

```bash
dotnet test tests/Integration --filter Category=Messaging
```

### V3 — Groups and membership revocation (US3)

1. Create a group with `alice`, `bob`, `carol`. Post messages.
2. Add `dave`. He sees history from his join point only, and the rule is shown to him.
3. Remove `carol` while her window is open. She stops receiving immediately, without refreshing,
   and loses access to the group's attachments.
4. Post to the seeded 500-member group; delivery to online members stays inside the budget.

### V4 — Notifications and unread state (US4 · SC-008)

1. Grant notification permission as `bob`, background the tab, have `alice` DM him. A notification
   arrives within 5 seconds and opens on the triggering message.
2. Post 20 ordinary messages to a group `bob` is in — no notification. Mention him — notification.
3. Read on one device; the unread badge clears on his other device.
4. Set a DND window and post inside it. No interruption, but unread state still updates.
5. Deny notification permission. The UI states plainly that `bob` will not be reached (FR-040).

### V5 — Attachments and access control (US5, US7 · SC-017)

1. Paste a screenshot into a group. All members see the inline preview.
2. Copy the attachment content URL and request it as a non-member. Expect refusal.
3. Upload the EICAR test file. It never becomes retrievable, and only the uploader is told.
4. Upload a 600 MB video. Rejected before any bytes transfer, with the limit stated.
5. Upload a valid MP4; playback starts before the whole file has downloaded (range requests).
6. Delete the message; the attachment stops being retrievable.

### V6 — Search (US6 · SC-007)

1. Search a phrase from a conversation you belong to. Results within 1 second, ranked, with context.
2. Search a phrase that exists only in a conversation you are **not** in. No results, and nothing —
   including response timing — reveals that the content exists.
3. Edit a message, then search its old content. No stale hit.
4. Have an admin remove you from a group, then search its phrases. Those results are gone.

> Run this against the seeded full-retention corpus, not the small dev dataset. Search that is fast
> on 10,000 rows tells you nothing about 125 million.

```bash
docker compose exec api dotnet run --project tools/Seeder -- --messages 125000000
k6 run tests/Load/search.js
```

### V7 — Meetings and screen sharing (US8, US9 · SC-009, SC-014)

1. Start a meeting from a conversation; other members see the join prompt.
2. Three participants join from separate machines. Two-way audio and video for all.
3. Mute and disable video — both propagate immediately.
4. Share a single application window, then switch applications. The other application is **not**
   revealed.
5. A second participant starts sharing. One defined rule applies and everyone is told which share
   they are viewing.
6. The starter leaves. The meeting continues.
7. A non-member with the meeting id requests a join token. Refused.
8. Load-test to 25 in one room, then to the 1,250 platform ceiling. Meetings in progress are
   unaffected when new starts are refused.

```bash
k6 run tests/Load/meetings.js
```

### V8 — Retention (SC-025)

1. Set the retention period to 1 month in a test environment and seed older data.
2. Run the sweep. Nothing newer than the boundary is deleted; nothing older survives.
3. Every deletion batch appears in the audit log.
4. The retention period is visible to ordinary employees in the UI (FR-053).

### V9 — Durability (SC-022, SC-023, SC-024)

1. `docker compose restart redis` under load. Latency rises; no message is lost or duplicated; no
   authorization decision becomes wrong.
2. `docker compose kill api` mid-send. The client retries and exactly one message lands.
3. Restore from backup into a clean stack and confirm a consistent state.

## Test suites

```bash
dotnet test tests/Unit           # No I/O. Fails if any test exceeds 100 ms.
dotnet test tests/Architecture   # The Dependency Rule + every endpoint declares a policy
dotnet test tests/Integration    # Testcontainers: PG, Redis, RabbitMQ, MinIO, Keycloak
dotnet test tests/Contract       # OpenAPI + event backward compatibility
npm --prefix src/internalchat-web test
npx playwright test              # E2E, including the two-client delivery timing harness
k6 run tests/Load/messaging.js   # Performance budget
```

Coverage floors are CI gates, not advice: Domain 90%, Application 85%, Infrastructure 60%,
React 80%. A merge that drops any layer below its floor fails the build.

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| Keycloak unhealthy on first run | Realm import races the database | It self-resolves; `docker compose logs keycloak` to confirm |
| Attachments stuck `pending` | ClamAV still loading signatures (~2 min cold) | Wait, then check `docker compose logs clamav` |
| Meetings show unavailable | Media host not started | `docker compose -f deploy/docker-compose.media.yml up -d` — and confirm messaging still worked without it |
| Web push does nothing on iOS | Known platform limitation (research D10) | Add to home screen. FR-040 requires the UI to have told you this already — if it did not, that is the bug |
| Search slow after seeding | `ANALYZE` not run on new partitions | `docker compose exec postgres psql -c 'ANALYZE messages;'` |
