# Phase 0 Research: Enterprise Internal Chat Platform

**Date**: 2026-07-31 | **Plan**: [plan.md](./plan.md) | **Constitution**: v1.2.0

Fourteen decisions. Every one was constrained by Principle VIII (free, self-hosted, OSI-licensed,
runs in Docker offline), which eliminated more options than any technical consideration did.

> **License note**: licenses below reflect the state at the time of writing. Constitution Principle
> VIII requires the license to be recorded in the pull request that introduces a dependency —
> re-verify at the moment you pin a version, because several .NET ecosystem projects have changed
> licensing recently (see D3).

---

## D1 — Message identity and ordering

**Decision**: Every message carries a client-generated `client_message_key` (ULID, supplied by the
sender) and a server-assigned per-conversation `seq` (monotonic `bigint`, allocated by
`UPDATE conversations SET last_seq = last_seq + 1 ... RETURNING last_seq` inside the same
transaction as the insert). Uniqueness is enforced by a composite unique index on
`(conversation_id, client_message_key)`.

**Rationale**: This single mechanism satisfies three requirements at once. FR-011 (exactly-once)
falls out of the unique index — a retried send hits the constraint and the handler returns the
existing message rather than inserting a second one. FR-012 (ordering independent of client clocks)
falls out of the server-side sequence. FR-013 (progressive history) becomes keyset pagination on
`(conversation_id, seq)`, which stays fast at 125 million rows where `OFFSET` would not.

**Alternatives considered**:

- *Timestamps for ordering* — rejected outright. Device clock skew reorders messages, and FR-012
  exists precisely to forbid this.
- *Snowflake/ULID as the sole order key* — globally sortable, no row lock, but leaves gaps. Gapless
  per-conversation sequences make "unread since seq N" and "deliver everything after seq N on
  reconnect" trivial and exact; with gaps, both become guesses.
- *Global sequence instead of per-conversation* — one hot sequence for the whole platform, and the
  pagination index becomes less selective. Per-conversation costs one row lock per send, which at
  100 messages/second spread across thousands of conversations is not contended. The 10,000-member
  all-hands conversation is the one hot spot; it is a broadcast channel with low write volume, so
  the lock is acceptable.

---

## D2 — Exactly-once delivery and the transactional outbox

**Decision**: `SendMessage` writes the message row and an `outbox_message` row in one PostgreSQL
transaction. A dispatcher in `InternalChat.Worker` polls the outbox (`FOR UPDATE SKIP LOCKED`,
batched) and publishes to RabbitMQ with publisher confirms, marking rows dispatched only after
confirmation. Consumers deduplicate on `(consumer_name, message_id)` in a processed-messages table.

**Rationale**: Constitution Principle VI forbids dual-writing a database change and a message
publish without atomicity. Without the outbox, a crash between commit and publish silently loses a
notification; a publish before commit can notify about a message that never existed. `SKIP LOCKED`
lets the dispatcher scale without a distributed lock.

**Alternatives considered**:

- *Publish inline from the handler* — the failure mode is silent and unrecoverable. Rejected by
  Principle VI directly.
- *PostgreSQL logical replication / CDC into RabbitMQ* — no application-level dual write at all, and
  genuinely elegant. Rejected as too much operational machinery (replication slots, Debezium, a
  Kafka-shaped hole) for a single-host deployment, and it would put a second stateful component in
  the critical path.
- *`LISTEN`/`NOTIFY` instead of RabbitMQ* — no delivery guarantee, no DLQ, no retry. Fails FR-024's
  scan pipeline and Principle VI's DLQ requirement.

---

## D3 — RabbitMQ client library

**Decision**: Use the official `RabbitMQ.Client` (Apache-2.0) with a thin `IEventPublisher`
abstraction and a hand-written consumer host in Infrastructure.

**Rationale**: **MassTransit v9 is commercially licensed.** This is a direct Principle VIII
violation and the single most likely way this project would have accidentally acquired a bill,
because MassTransit is the reflexive default for .NET messaging. MassTransit v8 remains
Apache-2.0 but pinning to a version that will stop receiving security fixes conflicts with the
constitution's requirement that dependencies be actively maintained and CVE-free.

The functionality actually needed — publish with confirms, consume with manual ack, DLQ, capped
retry, idempotency — is roughly 300 lines against the official client, and it keeps the retry and
dedupe semantics visible rather than buried in framework configuration.

**Alternatives considered**:

- *MassTransit v9* — rejected, commercial license.
- *MassTransit v8 pinned* — rejected, unmaintained-by-design is a supply-chain risk under
  Principle VIII's CVE requirement.
- *NServiceBus* — commercial. Rejected.
- *Rebus (MIT)* — genuinely viable and lighter than MassTransit. Rejected only because the official
  client plus 300 lines removes a dependency entirely, and this project's messaging needs are
  narrow. Revisit if consumer count grows past roughly a dozen.

---

## D4 — Real-time transport

**Decision**: ASP.NET Core SignalR over WebSockets, with the Redis backplane
(`Microsoft.AspNetCore.SignalR.StackExchangeRedis`). Group membership maps to conversation
membership. `ChatHub` contains no business logic — every method delegates to an Application use
case, as Principle I requires.

**Rationale**: Mandated by the constitution's stack table, and it fits: 7,000 concurrent
connections on one node is well inside what Kestrel handles, the Redis backplane is already a
required component, and automatic reconnection with negotiated fallback is built in.

**Key detail for FR-018**: the client sends its highest-seen `seq` per conversation on reconnect,
and the server replays everything above it. Reconnect is therefore a catch-up query, not a
best-effort replay of buffered events — which is what makes SC-022 ("never lost, never duplicated"
across reconnects) testable.

**Alternatives considered**:

- *Raw WebSockets* — more control, but reimplements reconnection, backplane fan-out, and transport
  negotiation. No benefit here.
- *Server-Sent Events* — one-directional; typing indicators and presence would need a second
  channel.

---

## D5 — Authentication and 5-minute revocation

**Decision**: Keycloak (Apache-2.0) as the OIDC provider. React uses authorization code with PKCE.
Access tokens live **5 minutes**; refresh tokens rotate. Keycloak back-channel logout and admin
deactivation events are consumed into a Redis revocation set keyed by session id, with a TTL equal
to the maximum token lifetime. The SignalR hub filter and the HTTP authorization filter both check
the revocation set.

**Rationale**: FR-003 demands revocation within 5 minutes, and the constitution caps access tokens
at 15 minutes. A 15-minute token cannot honour a 5-minute revocation promise on its own, so either
the token lifetime drops to 5 minutes or a revocation check runs on every request. We do both: the
short lifetime bounds the worst case, and the revocation set closes the window for the
already-connected SignalR case, where a long-lived connection would otherwise outlive the token.

Long-lived connections are the trap here. An employee deactivated at 09:00 with an open WebSocket
would keep receiving messages indefinitely without an explicit re-validation, which is precisely
the scenario SC-018 tests against.

**Alternatives considered**:

- *Introspection on every request* — exact, but adds a Keycloak round trip to every call and puts
  the IdP in the messaging hot path. Rejected on the p95 budget.
- *Local password store* — rejected by FR-001.
- *Authentik / Zitadel* — comparable and free. Keycloak chosen for maturity, Docker maturity, and
  LDAP/AD federation, which most enterprises with 10,000 employees already need.

---

## D6 — Authorization model

**Decision**: A single `RequireConversationMembership` authorization requirement, evaluated in the
Application layer against a membership record, applied by an endpoint filter and a hub filter. An
architecture test enumerates every mapped endpoint and hub method and fails the build if any lacks
an explicit policy. Membership lookups are cached in Redis for **30 seconds** and invalidated
explicitly inside `RemoveMember` and `DeleteConversation`.

**Rationale**: Principle IV requires deny-by-default and resource-scoped checks; Principle VII caps
authorization cache TTL at 60 seconds. The architecture test is what converts "we always add the
policy" from a habit into a build gate — the constitution says an endpoint without an explicit
policy must fail the build, and a review checklist does not do that.

30 seconds rather than the permitted 60 because SC-018's 5-minute revocation budget has to absorb
Keycloak propagation, our consumer, and the cache; spending half the budget on cache staleness
leaves no margin.

**Alternatives considered**:

- *Role-based only* — fails FR-002; roles say nothing about which conversation.
- *No cache* — one extra query per message delivery to a 10,000-member conversation is 10,000
  queries. Rejected on the delivery budget.
- *Cache membership in the JWT* — revocation becomes impossible within the token lifetime, and the
  token grows unbounded with conversation count.

---

## D7 — Attachment storage and access control

**Decision**: MinIO for objects. Retrieval flows through the API: the client requests
`GET /attachments/{id}/content`, the API performs a full membership check, then returns
`204 No Content` with an nginx `X-Accel-Redirect` header pointing at an internal-only location that
proxies MinIO. nginx streams the bytes; .NET never touches them. Upload is the mirror: the API
authorizes, the client `PUT`s to a quarantine location, and the object becomes retrievable only
after the ClamAV consumer records a clean verdict.

**Rationale**: This was the design that changed during the post-Phase-1 constitution re-check.
Presigned URLs — the obvious answer — fail FR-025 literally: a presigned URL is a bearer capability,
so a member can hand it to a non-member and it works. FR-025 says access is refused "regardless of
how the retrieval address was obtained". The internal-redirect pattern keeps the authorization
decision on every single request while keeping 500 MB video transfers off Kestrel's request threads,
which the p95 budget for messaging on the same host cannot survive.

**Alternatives considered**:

- *Presigned URLs with a 60-second TTL* — rejected, fails FR-025 as written. A 60-second window is
  still a window, and "we made the leak brief" is not the requirement.
- *Stream through Kestrel* — correct but blows the performance budget. 500 MB × concurrent
  downloads occupies threads that message delivery needs.
- *Serve MinIO directly with bucket policies* — MinIO cannot evaluate our conversation membership.

**License note**: MinIO server is AGPLv3 — fine for unmodified self-hosted use under Principle VIII,
which explicitly permits AGPL in that configuration. SeaweedFS (Apache-2.0) is the fallback if the
organization's policy forbids AGPL entirely; both speak S3, so `IObjectStore` is unaffected.

---

## D8 — Video playback without transcoding

**Decision**: Accept only browser-playable containers and codecs (MP4/H.264/AAC and WebM/VP9/Opus),
rejecting everything else at upload with the reason stated (FR-023). Serve with HTTP range requests
so playback starts immediately (FR-022). Use ffmpeg in the scan consumer for exactly one thing —
extracting a poster frame and remuxing MP4 to move the `moov` atom to the front when it is not
already there.

**Rationale**: Transcoding 500 MB videos would be the single largest CPU consumer on the
application host, competing directly with the messaging budget, on a host that is already
constrained to one machine. Restricting formats moves that cost to the uploader's machine, where it
is free. Range requests plus a front-loaded `moov` atom are what "plays without downloading the
whole file first" actually means in a browser.

**Alternatives considered**:

- *Full transcoding to HLS/DASH* — best playback experience, adaptive bitrate. Rejected: the CPU
  cost on a single shared host is incompatible with SC-006, and it is a large amount of machinery
  for a v1 that has no stated adaptive-bitrate requirement.
- *Accept anything, play what we can* — fails FR-023's "rejected before upload with the limit
  stated"; users would discover failure after a 500 MB upload.

---

## D9 — Full-text search

**Decision**: PostgreSQL full-text search — `tsvector` column maintained by trigger, GIN index,
`unaccent` extension for diacritic-insensitive matching, queried with the `simple` configuration
and filtered by the searcher's conversation memberships *before* ranking. Behind an
`ISearchIndex` interface in Application so the implementation is swappable.

**Rationale**: 125 million messages is a lot for PostgreSQL FTS in the abstract, but the query is
never unbounded: FR-029 requires results only from conversations the searcher belongs to, and a
typical employee belongs to tens of conversations, not thousands. Filtering on
`conversation_id = ANY(...)` first reduces the candidate set by orders of magnitude before the GIN
index is consulted. Monthly partitioning of the messages table keeps both search and the retention
sweep (D11) fast.

Adding OpenSearch would mean a second stateful system, a second backup story, and a second
consistency problem (FR-032 requires edits, deletions, and membership removals to be reflected in
results) — on a host that is already the only host.

**Vietnamese and diacritics**: the `simple` configuration plus `unaccent` handles Vietnamese
adequately because it is space-delimited; there is no stemming, which costs some recall on
inflected forms. This is the known limitation of the choice and is why the abstraction exists.

**Alternatives considered**:

- *OpenSearch (Apache-2.0)* — better relevance, typo tolerance, real analyzers. Rejected for v1 on
  Principle VIII's simplicity and the single-host constraint. **Trigger to revisit**: search p95
  exceeding 800 ms against seeded full-retention data, or a stated typo-tolerance requirement.
- *Meilisearch / Typesense* — excellent developer experience, small footprint. Same second-system
  objection, and neither is as straightforward to keep consistent with membership changes.

---

## D10 — Notifications

**Decision**: Browser Push (VAPID) via a service worker, plus in-app SignalR delivery for connected
clients. Fan-out happens in the Worker, driven by `chat.message.sent.v1` from the outbox. Unread
state lives in PostgreSQL (`read_state` per employee per conversation) and is broadcast to the
employee's other devices over SignalR, satisfying FR-036.

**Rationale**: Spec Q3 chose browser-only notifications, which keeps Principle VIII intact — VAPID
web push needs no third-party account, no developer program, and no fee. Fan-out belongs in the
Worker rather than the request path because notifying 10,000 members of an all-hands post must not
be inside the sender's 150 ms accept budget.

**FR-040 is the interesting one**: iOS delivers web push only for home-screen-installed web apps.
Rather than let affected employees silently believe they are reachable, the client detects
notification permission state and installation state and tells them plainly. This is a real product
limitation made visible, not hidden.

**Alternatives considered**:

- *Native apps with FCM/APNs* — rejected by spec Q3; APNs requires a paid Apple Developer Program,
  a Principle VIII violation.
- *Email fallback* — was spec option Q3-B, not chosen. The hook remains: `IPushSender` has a single
  responsibility and an email implementation would be additive.

---

## D11 — Retention

**Decision**: A nightly job in the Worker deletes messages and attachments older than a configurable
period, defaulting to 12 months, in bounded batches. The messages table is partitioned by month, so
the bulk of retention is `DROP PARTITION` rather than row-by-row `DELETE`. Every deletion batch
writes an audit event. Orphaned MinIO objects are reclaimed by a follow-up sweep.

**Rationale**: FR-052 through FR-054 plus SC-025's tolerance ("nothing deleted early, nothing
surviving more than 7 days past"). Partition dropping is what makes deleting roughly 10 million
rows a month a non-event rather than an overnight vacuum problem. The 7-day tolerance in SC-025 is
what makes a nightly batch job a legitimate implementation rather than requiring per-row expiry.

**Alternatives considered**:

- *Row-level `DELETE` with a `sent_at` index* — simple, but at 125 million rows the monthly delete
  and subsequent bloat would dominate the database's I/O budget.
- *TTL at the storage layer* — PostgreSQL has none, and doing it only in MinIO would leave message
  rows pointing at absent objects.

---

## D12 — Meetings and screen sharing

**Decision**: LiveKit (Apache-2.0) SFU on the dedicated media host, with TURN for restrictive
networks. The API mints short-lived LiveKit room tokens **only after** a conversation membership
check; room name is derived from the conversation id. `@livekit/components-react` on the frontend,
lazy-loaded so it stays out of the initial bundle. Screen sharing uses LiveKit's screen-share track
with a server-enforced single-publisher rule (FR-050).

**Rationale**: 25 participants per room with all cameras on is squarely SFU territory — mesh
(peer-to-peer) fails above roughly 5 participants because each client uploads N-1 streams. LiveKit
is Apache-2.0, ships as a Docker image, has a maintained .NET server SDK for token minting and
webhooks, and a first-class React component library, which matters because FR-045/046 (mute state,
bandwidth degradation) are exactly the things that are painful to build from raw WebRTC.

Token minting is the security boundary: LiveKit trusts its JWT completely, so FR-041's "only
members can join" is enforced entirely by our refusal to issue a token. That endpoint is on the
security sign-off list in the plan.

**Alternatives considered**:

- *Jitsi Videobridge (Apache-2.0)* — proven at scale and fully free. Rejected because integrating
  its authorization model with our membership check is more awkward than LiveKit's plain JWT, and
  the React integration story is heavier.
- *mediasoup (ISC)* — the most control, the best performance per core. Rejected because it is a
  library, not a server: adopting it means writing and operating a Node.js media service, which is a
  third runtime in a stack the constitution wants kept small.
- *Peer-to-peer mesh* — no media host needed, which would have preserved single-host deployment.
  Rejected on arithmetic: 25 participants × 24 outbound streams each is not survivable on a laptop
  uplink. This is the calculation that forced constitution amendment v1.2.0.

**Open item — media host capacity is UNMEASURED (T197).**

The 1,250 concurrent-participant ceiling (FR-043) is a *design* figure: 50 meetings × 25
participants, derived from the requirement rather than from any observation of hardware. Nothing
has yet established that a media host can carry it, and nothing in the test suite can:

- `tests/Load/meetings.js` (T196) measures the **API's** behaviour at the ceiling — that the 26th
  join is refused with 409 and that starts are refused with 503 once the counter is reached. k6
  mints tokens; it opens no WebRTC transports and consumes no SFU CPU. A green run there says the
  refusal logic is correct and says nothing about whether meetings work at scale.
- `tests/e2e/v8-meetings.spec.ts` (T198) exercises two browsers against a real SFU, which is real
  media but not load.

Closing this needs the actual media host and a WebRTC load generator that publishes tracks. Until
then the figure in `MeetingCapacityOptions.MaximumConcurrentParticipants` is configurable
specifically so the measured number can replace it without a code deployment.

**What to measure, and what would change:**

| Observation | Consequence |
| --- | --- |
| CPU saturates below 1,250 | Lower the configured ceiling to the measured figure. FR-044 already refuses at it, so the platform degrades by refusing rather than by everyone's call getting worse. |
| Uplink saturates first | The ceiling is bandwidth-bound, not CPU-bound; sizing the host differently is the answer, not a lower number. |
| 1,250 is comfortable | Record the headroom. The figure stays, and this open item closes. |

The measurement itself is the deliverable — the number matters less than knowing which resource
runs out first, because that is what decides whether the next host is bigger or there are two.

---

## D13 — Frontend architecture

**Decision**: React 19 + TypeScript `strict`, Vite, TanStack Query for server state, `react-virtuoso`
for message virtualization, route-level code splitting with the meetings feature lazy-loaded. A
service worker handles web push and nothing else.

**Rationale**: The 300 KB gzipped initial bundle budget is the forcing function. The LiveKit client
SDK alone is a substantial fraction of that budget, so meetings must be lazy — which is fortunate,
because meetings are P4 and most sessions never start one. Virtualization is required by the
constitution for lists over 100 items, and a chat history is the canonical case.

TanStack Query handles the cache-invalidation half of real-time UI: SignalR events invalidate or
patch query caches rather than maintaining a parallel state tree, which removes a whole class of
"the list and the badge disagree" bugs.

**Alternatives considered**:

- *Redux Toolkit* — more machinery for state that is mostly server state.
- *Next.js* — SSR buys little for an authenticated single-page application behind SSO, and adds a
  Node runtime to the deployment.

---

## D14 — Testing strategy

**Decision**: xUnit with NSubstitute for unit tests (no I/O, `TimeProvider` injected, under 100 ms
each). Testcontainers for integration against real PostgreSQL, Redis, RabbitMQ, MinIO, and
Keycloak. NetArchTest for the Dependency Rule and for the endpoint-policy-coverage assertion.
Vitest plus React Testing Library for frontend units, Playwright for cross-browser end-to-end
including the two-client delivery timing harness, and k6 for each performance budget row.

**Rationale**: Constitution Principle III forbids mocking PostgreSQL, Redis, and RabbitMQ in
integration tests, so Testcontainers is not a preference but a requirement. The architecture tests
are what make Principles I and IV enforceable rather than aspirational — specifically the test that
fails the build when an endpoint is mapped without an authorization policy.

The two-client Playwright harness deserves a mention: SC-006's end-to-end delivery budget cannot be
measured from the server side, because the thing being measured is the interval between one user
pressing send and another user's DOM updating.

**Alternatives considered**:

- *In-memory EF Core provider* — rejected. It does not implement PostgreSQL semantics, so
  constraint violations, `SKIP LOCKED`, full-text search, and partitioning all behave differently.
  Constitution Principle III forbids it for integration tests specifically because of this.
- *Shared long-lived test database* — faster startup, but tests become order-dependent and CI
  becomes flaky, which Principle III gives one working day to fix.

---

## Dependency licence register

Recorded here so the pull requests that introduce these dependencies can cite it, per Principle VIII.
Re-verify each at pin time.

| Component | Licence | Cost | Notes |
| --- | --- | --- | --- |
| .NET 10 / ASP.NET Core 10 | MIT | Free | |
| PostgreSQL 17 | PostgreSQL Licence | Free | |
| Redis 7 | Source-available (AGPLv3 option in 8.x) | Free self-hosted | Valkey (BSD-3) is the approved swap per constitution |
| RabbitMQ 4 | MPL-2.0 | Free | |
| `RabbitMQ.Client` | Apache-2.0 | Free | Chosen over MassTransit — see D3 |
| Keycloak | Apache-2.0 | Free | |
| MinIO server | AGPLv3 | Free self-hosted | Unmodified self-hosted use only; SeaweedFS is the fallback |
| LiveKit server | Apache-2.0 | Free self-hosted | LiveKit **Cloud** is paid — do not use |
| ClamAV | GPL-2.0 | Free | Separate daemon, not linked |
| nginx | BSD-2-Clause | Free | |
| React, Vite, TanStack Query, react-virtuoso | MIT | Free | |
| Testcontainers for .NET, NetArchTest, NSubstitute | MIT | Free | |
| YamlDotNet | MIT | Free | Test-side only — the contract suite reads `contracts/openapi.yaml` so it asserts against the committed document rather than a copy of it in C# |
| `Lib.Net.Http.WebPush` | MIT | Free | T134 — RFC 8291/8292 VAPID web push. Talks directly to whatever endpoint the browser subscription names; no account, SDK, or fee |
| Prometheus, Grafana, Loki, Jaeger, OpenTelemetry | Apache-2.0 / AGPLv3 (Loki, Grafana) | Free self-hosted | |
| Trivy, Gitleaks, Semgrep OSS | Apache-2.0 / MIT / LGPL-2.1 | Free | |
| k6 | AGPL-3.0 | Free | Used as a tool, not linked |

**Rejected for cost**: MassTransit v9 (commercial), NServiceBus (commercial), LiveKit Cloud, any
hosted APM, any managed database or queue, Apple Developer Program (would have been required by
spec option Q3-C).

## Open items carried into implementation

None blocking. Two items to measure rather than decide now:

1. **Search headroom** — D9's PostgreSQL FTS choice must be validated against a seeded 125-million-row
   corpus during the US6 phase, not assumed. If p95 exceeds 800 ms, `ISearchIndex` is the seam where
   OpenSearch goes in.
2. **Media host sizing** — D12's LiveKit capacity for 1,250 concurrent participants must be measured
   on the actual hardware before US8 is declared done. Participant ceilings are CPU- and
   uplink-bound and no published figure substitutes for measuring the real host.
