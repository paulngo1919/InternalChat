# Implementation Plan: Enterprise Internal Chat Platform

**Branch**: `001-enterprise-chat-platform` (repository is not git-initialized; the spec directory is
the identity) | **Date**: 2026-07-31 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-enterprise-chat-platform/spec.md`

## Summary

Deliver a self-hosted internal chat platform for 10,000 employees: corporate SSO, one-to-one and
group messaging with exactly-once real-time delivery, image and video attachments, browser
notifications, full-text search over 12 months of history, and video meetings with screen sharing
for up to 25 participants.

The technical approach is a Clean Architecture ASP.NET Core 10 backend with a React 19 frontend,
PostgreSQL as the sole source of truth, Redis as cache and SignalR backplane, and RabbitMQ for all
side effects behind a transactional outbox. Everything runs under Docker Compose on one
application host, with a single dedicated media host running LiveKit for meetings — the only
second host the constitution permits.

Two decisions drive most of the design. **Message identity is client-supplied and server-ordered**
— a client idempotency key plus a per-conversation server sequence — which is what makes
exactly-once delivery (FR-011) and gapless pagination possible at all. **Authorization is always a
database-backed membership check**, never served from a long-lived cache and never delegated to a
bearer URL, which is what makes FR-025 and SC-017 honest rather than aspirational.

## Technical Context

**Language/Version**: C# 14 on .NET 10 (LTS); TypeScript 5.x on React 19

**Primary Dependencies**: ASP.NET Core 10 (Minimal APIs + SignalR), EF Core 10 + Npgsql,
StackExchange.Redis, RabbitMQ.Client (official, Apache-2.0), LiveKit Server SDK, MinIO .NET SDK,
web-push library with VAPID; React 19, Vite, TanStack Query, react-virtuoso,
`@livekit/components-react`

**Storage**: PostgreSQL 17 (source of truth, including full-text search), Redis 7 (cache, SignalR
backplane, presence, token revocation set), MinIO (attachment objects)

**Testing**: xUnit + NSubstitute (unit), Testcontainers for PostgreSQL/Redis/RabbitMQ/MinIO/
Keycloak (integration), NetArchTest (architecture), Vitest + React Testing Library (frontend unit),
Playwright (E2E), k6 (load)

**Target Platform**: Linux containers under Docker Compose. Application host runs API, worker, web,
PostgreSQL, Redis, RabbitMQ, Keycloak, MinIO, ClamAV, nginx, and the observability stack. Media
host runs LiveKit SFU + TURN — separate, per constitution v1.2.0.

**Project Type**: Web application — Clean Architecture backend (4 layers plus a worker host) with a
React SPA frontend

**Performance Goals**: message send accept p95 150 ms / p99 300 ms; end-to-end delivery p95 500 ms;
history page of 50 messages p95 250 ms; search p95 800 ms; 7,000 concurrent SignalR connections;
100 messages/second sustained with 1,000/second burst; meetings at 25 participants per room and
1,250 concurrent participants platform-wide

**Constraints**: Everything free and self-hostable (Principle VIII). One application host plus one
media host, no Kubernetes. Access revocation within 5 minutes. 12-month hard retention. Initial JS
bundle under 300 KB gzipped. The application must stay fully functional with the media host down.

**Scale/Scope**: 10,000 employees, 7,000 peak concurrent, roughly 125 million messages retained at
steady state (10,000 × 50/day × 250 working days), on a 12-month rolling window

### Declared performance budget for this feature

| Operation | p95 | p99 | Measured by |
| --- | --- | --- | --- |
| `POST /conversations/{id}/messages` accept | 150 ms | 300 ms | k6 against the Compose stack |
| Delivery to all online recipients (end-to-end) | 500 ms | 1 s | Playwright two-client timing harness |
| `GET /conversations/{id}/messages` (50 items) | 250 ms | 500 ms | k6 against a fully seeded database |
| `GET /search` across full retention | 800 ms | 1.5 s | k6 against a seeded corpus |
| Meeting audio path | 300 ms | — | LiveKit metrics on the media host |
| Frontend LCP / INP (p75) | 2.5 s / 200 ms | — | Lighthouse CI in the pipeline |

Load tests run against a database seeded to full retention volume, not an empty one. A budget
verified on a thousand rows is not verified.

### Declared security surface for this feature

- **AuthN changes**: new — OIDC authorization code with PKCE against Keycloak; JWT validation on
  HTTP, on SignalR connect *and* on reconnect; back-channel logout consumed into a Redis
  revocation set.
- **AuthZ changes**: new — every endpoint, hub method, and consumer carries an explicit
  resource-scoped membership policy. No role-only checks anywhere.
- **New data classes handled**: message bodies (confidential), attachment binaries (confidential),
  employee directory attributes (personal), presence (personal), audit events (regulated).
- **New external inputs**: message text, file uploads, search queries, meeting join requests,
  Keycloak tokens and logout callbacks, LiveKit webhooks.
- **Audit events emitted**: sign-in, sign-out, access denial, conversation create, membership
  add/remove, role change, export, retention deletion, retention-policy change, meeting
  start/join/leave/end, attachment scan verdict.
- **Second reviewer with security sign-off required** for: the authorization pipeline, the
  attachment access path, the LiveKit token-minting endpoint, and the revocation mechanism.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

*Source: `.specify/memory/constitution.md` v1.2.0. Table below reflects the post-Phase-1 state.*

| # | Gate | Status | Notes |
| --- | --- | --- | --- |
| I | **Clean Architecture** | PASS | Four layers plus a Worker host that references Application only. Domain has zero non-BCL packages. DTOs at every boundary; no entity is serialized. Enforced by NetArchTest, not by convention. |
| II | **SOLID** | PASS | One handler per use case. Outbound interfaces (`IMessageRepository`, `ICacheStore`, `IEventPublisher`, `IObjectStore`, `IMeetingTokenIssuer`, `ISearchIndex`, `IPushSender`, `IAuditLog`) are consumer-owned and at most five members each. |
| III | **Test-First** | PASS | Coverage floors wired as CI gates before the first handler is written. Testcontainers fixtures land in the Foundational phase so no story can be built without an integration path available. |
| IV | **Security by Default** | PASS | Deny-by-default via a `RequireConversationMembership` policy applied by an endpoint filter; an architecture test asserts every endpoint declares a policy, so omission fails the build. Attachment bytes never leave storage without a per-request check. |
| V | **Performance Budgets** | PASS | Budget table above. History and search are keyset-paginated with a hard maximum page size of 100. N+1 prevention is enforced by an EF Core interceptor that fails integration tests on query count per request. |
| VI | **Messaging Contracts** | PASS | Transactional outbox for every user-visible event. Consumers idempotent on `(consumer_name, message_id)`. DLQ plus capped exponential retry per queue. Contracts versioned as `chat.message.sent.v1`. |
| VII | **Data Authority & Cache** | PASS | PostgreSQL authoritative. Every Redis key carries a TTL. Membership cache TTL is 30 s — under the 60 s authorization ceiling — and is invalidated inside the mutating use case. Migrations are forward-only and run as a separate step. |
| VIII | **Zero-Cost & Self-Hosted** | PASS | Every component OSI-licensed and self-hostable. **MassTransit rejected** — v9 is commercially licensed; the official `RabbitMQ.Client` is used instead. Licenses recorded in [research.md](./research.md). |
| — | **Stack** | PASS | Mandated stack used as-is. Additions are LiveKit (meetings), ClamAV (FR-024), and nginx (TLS plus attachment offload), each justified in Complexity Tracking. |
| — | **Docker-First** | PASS | `docker compose up` brings up the full application host. The media host is a second Compose file. The API degrades to "meetings unavailable" when LiveKit is unreachable — covered by an integration test, not just by intent. |

**Result: PASS.** Four deviations are recorded in Complexity Tracking, each with an owner and a
removal date.

## Project Structure

### Documentation (this feature)

```text
specs/001-enterprise-chat-platform/
├── plan.md              # This file (/speckit-plan command output)
├── spec.md              # Feature specification
├── research.md          # Phase 0 output — 14 decisions with rationale
├── data-model.md        # Phase 1 output — entities, constraints, indexes, state transitions
├── quickstart.md        # Phase 1 output — how to run and validate the system
├── contracts/           # Phase 1 output
│   ├── README.md        # Contract index and versioning policy
│   ├── openapi.yaml     # HTTP API contract
│   ├── signalr-hub.md   # Real-time hub contract
│   └── messaging.md     # RabbitMQ event contracts
├── checklists/
│   └── requirements.md  # Spec quality checklist (18/18 pass)
└── tasks.md             # Phase 2 output (/speckit-tasks — NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/
├── InternalChat.Domain/              # Entities, value objects, domain events, invariants
│   ├── Conversations/                # Conversation, Membership, HistoryVisibility
│   ├── Messages/                     # Message, MessageBody, ClientMessageKey, EditWindow
│   ├── Attachments/                  # Attachment, ScanVerdict, FileConstraints
│   ├── Meetings/                     # Meeting, Participation, ShareSession
│   ├── Employees/                    # Employee, EmployeeStatus, Presence
│   ├── Notifications/                # NotificationReason, DoNotDisturbWindow, MuteSetting
│   ├── Audit/                        # AuditEvent
│   └── Common/                       # Entity, ValueObject, DomainEvent, IClock
│
├── InternalChat.Application/         # Use cases plus outbound interfaces. References Domain only.
│   ├── Abstractions/                 # IMessageRepository, ICacheStore, IEventPublisher,
│   │                                 #   IObjectStore, IMeetingTokenIssuer, ISearchIndex,
│   │                                 #   IPushSender, IAuditLog, IUnitOfWork
│   ├── Authorization/                # ConversationMembershipRequirement and its evaluator
│   ├── Conversations/                # CreateConversation, AddMember, RemoveMember, ListForUser
│   ├── Messages/                     # SendMessage, EditMessage, DeleteMessage, GetHistory
│   ├── Attachments/                  # RequestUpload, CompleteUpload, AuthorizeDownload
│   ├── Search/                       # SearchMessages
│   ├── Notifications/                # MarkRead, UpdatePreferences, FanOutNotification
│   ├── Meetings/                     # StartMeeting, IssueJoinToken, EndMeeting
│   ├── Retention/                    # ApplyRetentionPolicy, ExportConversation
│   └── Behaviors/                    # Validation, logging, transaction, audit pipeline behaviors
│
├── InternalChat.Infrastructure/      # Implements Application interfaces. Referenced by nobody.
│   ├── Persistence/                  # DbContext, configurations, repositories, outbox, migrations
│   ├── Caching/                      # Redis cache store, membership cache, revocation set
│   ├── Messaging/                    # RabbitMQ publisher, consumer host, outbox dispatcher, DLQ
│   ├── Storage/                      # MinIO object store, internal-redirect handoff
│   ├── Search/                       # PostgreSQL full-text search index
│   ├── Meetings/                     # LiveKit token issuer and webhook verifier
│   ├── Push/                         # VAPID web push sender
│   ├── Identity/                     # Keycloak token validation, back-channel logout
│   └── Scanning/                     # ClamAV client
│
├── InternalChat.Api/                 # Endpoints, hubs, DTOs. References Application only.
│   ├── Endpoints/                    # Minimal API groups per resource
│   ├── Hubs/                         # ChatHub — delegates to use cases, holds no logic
│   ├── Contracts/                    # Request and response DTOs
│   ├── Authorization/                # Policy registration, endpoint filter, hub filter
│   └── Program.cs                    # Composition root — the only place Infrastructure is named
│
├── InternalChat.Worker/              # Single-replica scheduled and queue-consuming host
│   ├── Consumers/                    # Notification fan-out, attachment scan, search index, audit
│   ├── Jobs/                         # Retention sweep, storage-capacity alert, outbox dispatcher
│   └── Program.cs
│
└── internalchat-web/                 # React 19 + TypeScript
    ├── src/
    │   ├── features/                 # conversations, messages, attachments, search,
    │   │                             #   notifications, meetings (lazy-loaded)
    │   ├── components/               # Shared presentational components
    │   ├── lib/                      # API client, SignalR client, auth, service worker
    │   └── routes/
    └── tests/

tests/
├── Unit/                             # Domain and Application. No I/O, no clock, under 100 ms each.
│   ├── Domain/
│   └── Application/
├── Integration/                      # Testcontainers: PG, Redis, RabbitMQ, MinIO, Keycloak
├── Contract/                         # OpenAPI and message-contract backward compatibility
├── Architecture/                     # NetArchTest — the Dependency Rule, policy coverage
└── Load/                             # k6 scripts, one per budget row

deploy/
├── docker-compose.yml                # Application host — the default `docker compose up`
├── docker-compose.media.yml          # Media host — LiveKit SFU and TURN
├── docker-compose.observability.yml  # Prometheus, Grafana, Loki, Jaeger, OTel Collector
├── nginx/                            # TLS termination and internal-redirect attachment offload
├── keycloak/                         # Realm export with development users and roles
└── .env.example
```

**Structure Decision**: Clean Architecture across five backend projects plus one frontend package,
mapping directly to Constitution Principle I. The fifth backend project (`InternalChat.Worker`) is
a deployment boundary rather than an architectural layer — it exists because Principle V forbids
in-process scheduling that breaks under multiple API replicas, and because retention sweeps and
attachment scanning must not compete with request-path CPU. It references Application only, exactly
as `InternalChat.Api` does, so the Dependency Rule is unchanged and the architecture test covers
both hosts identically.

The `deploy/` tree sits beside `src/` rather than inside it, so the Docker-first requirement is a
first-class artifact rather than files scattered next to code.

## Complexity Tracking

> Filled because the Constitution Check records four additions beyond the mandated stack.

| Violation | Why Needed | Simpler Alternative Rejected Because | Owner | Removal Date |
| --- | --- | --- | --- | --- |
| Second host running LiveKit (media relay) | FR-042/043 require 25 participants per meeting and 1,250 concurrent platform-wide | Colocating on the application host was rejected because a busy meeting hour consumes the CPU that SC-006's 500 ms delivery budget depends on, degrading messaging for all 10,000 employees to serve at most 1,250. Explicitly permitted by constitution v1.2.0. | Platform lead | Reassess 2027-01-31 — remove if measured meeting load stays under 200 concurrent participants |
| nginx internal redirect for attachment bytes | FR-025 requires a membership check on every retrieval, and videos reach 500 MB | Presigned MinIO URLs were rejected because a presigned URL is a bearer capability — anyone holding it retrieves the file, which fails FR-025 literally. Streaming through Kestrel was rejected because 500 MB transfers consume request-path threads and blow the messaging p95 budget on the same host. | Backend lead | Permanent — this is the correct pattern, not a shortcut |
| ClamAV container | FR-024 requires malware scanning before an upload becomes retrievable | No alternative exists: this is a stated functional requirement, and no free self-hosted scanner avoids a separate daemon | Security reviewer | Permanent |
| `InternalChat.Worker` as a fifth backend project | Retention sweeps, attachment scanning, notification fan-out, and the outbox dispatcher must run exactly once regardless of API replica count | A `BackgroundService` inside the API was rejected because it duplicates work per replica and violates the stateless-API rule; a Redis distributed lock was rejected as more moving parts than a single-replica container | Backend lead | Permanent |

## Phase Sequencing

Design artifacts are complete for all nine user stories, but implementation is expected to land in
constitution-compliant increments. `/speckit-tasks` produces the task breakdown; the intended order
is:

1. **Foundational** — solution skeleton, layers, architecture tests, Compose stack, Testcontainers
   fixtures, Keycloak realm, outbox and RabbitMQ topology, observability, CI gates.
2. **US1 + US2 (P1)** — auth, authorization pipeline, conversations, exactly-once send, real-time
   delivery, history pagination. This is the MVP, and the point at which the performance budget is
   first measured for real.
3. **US3 + US4 + US5 (P2)** — groups, notifications and unread state, image attachments.
4. **US6 + US7 (P3)** — search, video attachments.
5. **US8 + US9 (P4)** — media host, meetings, screen sharing. Gated on the application host meeting
   its budgets first, so meeting work can never mask a messaging regression.

## Post-Design Constitution Re-Check

Re-evaluated after `data-model.md` and `contracts/` were written. Two design decisions changed as a
direct result:

1. **Attachment access originally used presigned URLs.** The Principle IV re-check caught that this
   fails FR-025 — a presigned URL works for whoever holds it, member or not. Redesigned to an
   authorize-then-internal-redirect handoff so the membership check happens on every retrieval while
   the bytes still never pass through .NET.
2. **Membership cache TTL was originally 5 minutes.** The Principle VII re-check caught the 60 s
   authorization ceiling. Reduced to 30 s with explicit invalidation inside `RemoveMember`, which is
   also what makes SC-018's 5-minute revocation provable rather than hopeful.

No unresolved violations. Gate status remains PASS.
