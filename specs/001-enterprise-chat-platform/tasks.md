---

description: "Task list for Enterprise Internal Chat Platform"
---

# Tasks: Enterprise Internal Chat Platform

**Input**: Design documents from `/specs/001-enterprise-chat-platform/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/](./contracts/)

**Tests**: Tests are MANDATORY per Constitution Principle III (Test-First & Unit Test Coverage,
NON-NEGOTIABLE). Every user story includes test tasks, they are ordered before the implementation
they cover, and they MUST be observed failing first. Integration tests for repository, cache,
messaging, auth, and contract paths run against real PostgreSQL, Redis, RabbitMQ, MinIO, and
Keycloak via Testcontainers — mocking those is prohibited.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (US1–US9)
- Every task includes an exact file path

## Path Conventions

- **Backend (Clean Architecture)**: `src/InternalChat.Domain/`, `src/InternalChat.Application/`,
  `src/InternalChat.Infrastructure/`, `src/InternalChat.Api/`, `src/InternalChat.Worker/`
- **Frontend**: `src/internalchat-web/src/`
- **Tests**: `tests/Unit/`, `tests/Integration/`, `tests/Contract/`, `tests/Architecture/`,
  `tests/Load/`
- **Deployment**: `deploy/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution skeleton, tooling, and the Docker stack that everything else assumes

- [X] T001 Create solution and directory layout per plan.md in `InternalChat.slnx`
- [X] T002 [P] Create Domain project with zero non-BCL package references in `src/InternalChat.Domain/InternalChat.Domain.csproj`
- [X] T003 [P] Create Application project referencing Domain only in `src/InternalChat.Application/InternalChat.Application.csproj`
- [X] T004 [P] Create Infrastructure project referencing Application in `src/InternalChat.Infrastructure/InternalChat.Infrastructure.csproj`
- [X] T005 [P] Create Api project referencing Application, plus Infrastructure **for DI registration only** — usage outside `Program.cs` is failed by T018 — in `src/InternalChat.Api/InternalChat.Api.csproj`
- [X] T006 [P] Create Worker project referencing Application, plus Infrastructure **for DI registration only** — same constraint as T005 — in `src/InternalChat.Worker/InternalChat.Worker.csproj`
- [X] T007 Configure nullable, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, and central package management in `Directory.Build.props` and `Directory.Packages.props`
- [X] T008 [P] Scaffold React 19 + TypeScript strict + Vite in `src/internalchat-web/`
- [X] T009 [P] Configure ESLint, Prettier, and `tsconfig.json` with `strict: true` and no implicit `any` in `src/internalchat-web/`
- [X] T010 [P] Create test projects in `tests/Unit/`, `tests/Integration/`, `tests/Contract/`, `tests/Architecture/`
- [X] T011 Create the application-host stack with pinned image tags and health checks in `deploy/docker-compose.yml` (postgres, redis, rabbitmq, keycloak, minio, clamav, nginx, api, worker, web)
- [X] T012 [P] Create non-secret placeholders in `deploy/.env.example` and add `deploy/.env` to `.gitignore`
- [X] T013 [P] Create multi-stage non-root Dockerfiles with `HEALTHCHECK` in `deploy/Dockerfile.api`, `deploy/Dockerfile.worker`, `deploy/Dockerfile.web` — **image builds not yet executed; verify with `docker compose build` before relying on them**
- [X] T014 Configure per-layer coverage thresholds (Domain 90 / Application 85 / Infrastructure 60) in `coverlet.runsettings` plus `tools/Check-Coverage.ps1` — coverlet supports only one threshold per run, so the four per-layer floors are enforced by the script; missing data fails rather than passes
- [X] T015 Create the CI pipeline with all 10 constitution gates in `.github/workflows/ci.yml`

**Checkpoint**: Phase 1 complete (T001–T015). Solution builds clean, frontend builds clean,
Compose stack validates, all ten CI gates defined.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Guardrails and cross-cutting infrastructure that MUST exist before any user story

**⚠️ CRITICAL**: No user story work can begin until this phase is complete. The architecture tests
come first deliberately — they are what make Principles I and IV enforceable rather than habitual.

### Architecture guardrails

- [X] T016 [P] Architecture test asserting Domain references nothing outward in `tests/Architecture/DependencyRuleTests.cs`
- [X] T017 [P] Architecture test asserting Application never references Infrastructure in `tests/Architecture/DependencyRuleTests.cs`
- [X] T018 [P] Architecture test asserting Api and Worker name Infrastructure types only in `Program.cs` in `tests/Architecture/CompositionRootTests.cs`
- [X] T019 [P] Architecture test failing the build when any mapped endpoint or hub method lacks an explicit authorization policy in `tests/Architecture/AuthorizationCoverageTests.cs` — **verified by deliberately adding an unprotected endpoint and confirming 2 tests failed**
- [X] T020 [P] Architecture test asserting no Domain entity type appears in an Api contract DTO in `tests/Architecture/BoundaryTests.cs`

### Domain and Application primitives

- [X] T021 [P] Create `Entity`, `ValueObject`, `DomainEvent` base types in `src/InternalChat.Domain/Common/`
- [X] T022 [P] Create `IClock` with a `TimeProvider` adapter in `src/InternalChat.Domain/Common/IClock.cs`
- [X] T023 [P] Define outbound interfaces (`IUnitOfWork`, `ICacheStore`, `IEventPublisher`, `IObjectStore`, `IMeetingTokenIssuer`, `ISearchIndex`, `IPushSender`, `IAuditLog`, plus `IValidator<T>`) in `src/InternalChat.Application/Abstractions/`
- [X] T024 [P] Create validation, logging, transaction, and audit pipeline behaviors in `src/InternalChat.Application/Behaviors/` — hand-rolled dispatcher, **not MediatR** (v13+ is commercially licensed; same Principle VIII trap as MassTransit)

### Persistence

- [X] T025 Create `ChatDbContext` and configuration conventions in `src/InternalChat.Infrastructure/Persistence/ChatDbContext.cs` — snake_case, `timestamptz`, bounded strings, assembly-scanned configurations, plus `ChatDbContextFactory` for design-time tooling
- [X] T026 Create the initial migration with monthly `RANGE` partitioning for `messages` in `src/InternalChat.Infrastructure/Persistence/Migrations/` — extensions plus the three partition-management functions; the `messages` table itself attaches to them in T088, since the entity does not exist until then. **Verified against real PostgreSQL: applied cleanly, partitions create/route/drop correctly, re-apply is a no-op**
- [X] T027 Create the migration runner as a separate idempotent Compose step in `deploy/Dockerfile.migrations` and `deploy/docker-compose.yml` — EF migration bundle, non-root, `restart: 'no'`; `api` and `worker` gated on `service_completed_successfully`. ⚠️ **image not yet built**
- [X] T028 [P] Create an EF Core interceptor that fails integration tests when a request exceeds its query-count budget in `src/InternalChat.Infrastructure/Persistence/QueryCountInterceptor.cs`

### Test infrastructure

- [X] T029 Create the Testcontainers fixture for PostgreSQL, Redis, RabbitMQ, MinIO, and Keycloak in `tests/Integration/Fixtures/StackFixture.cs` — collection fixture, parallel start, image tags pinned to `docker-compose.yml`. **Verified by 7 smoke tests against real containers**
- [X] T030 [P] Create the integration test base with per-test schema reset in `tests/Integration/IntegrationTestBase.cs` — truncate + Redis flush; migration history excluded by case-insensitive prefix
- [X] T031 [P] Create a unit-test guard that fails any test exceeding 100 ms in `tests/Unit/UnitTestBase.cs` — **verified: guard fires over budget, stays quiet under it, idempotent on double dispose**

### Caching

- [X] T032 Implement `ICacheStore` over Redis, rejecting any write without a TTL, in `src/InternalChat.Infrastructure/Caching/RedisCacheStore.cs` — also rejects TTL above a 24h ceiling (catches `FromDays`/`FromSeconds` unit slips); `SCAN`-based prefix invalidation, environment-namespaced keys. Plus `InfrastructureServiceCollectionExtensions.AddInfrastructure()`
- [X] T033 [P] Integration test asserting a TTL-less cache write is refused in `tests/Integration/Caching/RedisCacheStoreTests.cs` — 12 tests against real Redis, covering zero/negative/over-ceiling TTL, prefix invalidation scope, namespacing, and unreadable-entry-as-miss

### Messaging (Principle VI)

- [X] T034 Create `outbox_message` and `processed_message` tables with EF configurations in `src/InternalChat.Infrastructure/Persistence/Configurations/` — partial index on pending rows; composite PK on `processed_message` IS the dedup mechanism. **Verified: both migrations applied to a fresh database, `payload` is `jsonb` with no length cap**
- [X] T035 Implement `IEventPublisher` writing to the outbox inside the ambient transaction in `src/InternalChat.Infrastructure/Messaging/OutboxEventPublisher.cs` — does not touch RabbitMQ and does not call `SaveChanges`; the pipeline's transaction behavior owns the commit
- [X] T036 Declare exchanges, queues, DLX, and capped retry per `contracts/messaging.md` in `src/InternalChat.Infrastructure/Messaging/ChatTopology.cs` — TTL-based retry queues (1s/5s/25s) with a **separate direct requeue exchange** so a retry returns to one queue instead of re-fanning to all; per-queue DLQ so depth alerts identify the consumer
- [X] T037 Implement the outbox dispatcher using `FOR UPDATE SKIP LOCKED` with publisher confirms in `src/InternalChat.Infrastructure/Messaging/OutboxDispatcher.cs` plus `RabbitMqConnectionProvider` — rows marked dispatched only after broker confirmation
- [X] T038 Implement the consumer host with manual ack and `processed_message` idempotency in `src/InternalChat.Infrastructure/Messaging/ConsumerHost.cs` — dedup insert (`ON CONFLICT DO NOTHING`) shares the handler's transaction, so a failed handler rolls back the dedup record; never nacks with requeue
- [X] T039 Integration test asserting a publish failure leaves the outbox row undispatched and redelivers exactly once in `tests/Integration/Messaging/OutboxTests.cs` — 6 tests against real PostgreSQL + RabbitMQ, including rollback-never-publishes and payload-carries-no-message-content
- [X] T040 [P] Integration test asserting a redelivered message is processed exactly once in `tests/Integration/Messaging/ConsumerHostTests.cs` — 3 tests: triple delivery handled once, per-consumer dedup keying, failed handler rolls back the dedup record
- [X] T041 [P] Integration test asserting an exhausted retry lands in the DLQ rather than disappearing in `tests/Integration/Messaging/ConsumerHostTests.cs` — 4 tests: no immediate requeue, TTL return with attempt counter carried, DLQ arrival with failure reason, message never lost. **Suite run 3× consecutively with no flakes**

### API cross-cutting

- [X] T042 Implement the Problem Details (RFC 9457) exception handler with no stack traces or SQL in `src/InternalChat.Api/Middleware/ProblemDetailsHandler.cs` — known exceptions mapped deliberately, everything else collapsed to a bare 500 with a trace id; `UnauthorizedAccessException` returns a **404-shaped** body so a refusal never reveals the resource exists (SC-017). Wired in `Program.cs`. ⚠️ no test yet — contract tests land in T054
- [X] T043 [P] Configure per-user and per-IP rate limit policies for auth, send, search, and upload in `src/InternalChat.Api/RateLimiting/` — partitioned by authenticated subject falling back to IP, so one heavy user cannot exhaust a shared office egress address for the whole floor; auth is IP-only because the caller is by definition not yet authenticated. Limiter shape chosen per surface: fixed window for auth and search, token bucket for send (typing three messages in a row is a legitimate burst), **concurrency** for upload (the cost is a 500 MB transfer held open, not the request rate). Global backstop so a new endpoint is never unlimited by omission; rejections carry `Retry-After` and a Problem Details body. Wired in `Program.cs`
- [X] T044 [P] Configure CSP without `unsafe-inline`/`unsafe-eval`, HSTS, nosniff, Referrer-Policy, frame-deny, and a CORS allow-list in `deploy/nginx/nginx.conf` plus `deploy/nginx/cors.inc` — all headers use `always` so they survive error responses; unlisted CORS origins map to empty, never `*`. **Config validated with `nginx -t`**
- [X] T045 Configure OpenTelemetry traces, metrics, and logs with W3C context propagated across HTTP, SignalR, and RabbitMQ in `src/InternalChat.Api/Program.cs` and `src/InternalChat.Worker/Program.cs` — per-host setup (Api instruments ASP.NET Core, Worker does not), sharing activity-source and meter names via `src/InternalChat.Application/Telemetry/ChatTelemetry.cs`, since Infrastructure creates the messaging spans and Principle I forbids either host naming an Infrastructure type outside `Program.cs`. **Carrying `traceparent` is not the same as joining the trace**: `OutboxDispatcher` now opens a producer span parented to the row's stored context and puts *its own* id on the wire, and `ConsumerHost` opens a consumer span parented to the delivered header. Covered by `tests/Integration/Observability/TraceContinuityTests.cs` — **3 tests, each verified to fail when its parsing is removed.** The first draft of those tests passed against a deliberately broken implementation because `Activity.Current` was still ambient; they now null it out to reproduce the Worker's actual broker-callback conditions. SignalR needs no extra package — hub calls arrive over the negotiated HTTP connection; the hub-method span lands with T097
- [X] T046 [P] Create the observability stack (OTel Collector, Prometheus, Grafana, Loki, Jaeger) in `deploy/docker-compose.observability.yml` — separate file, because losing observability must degrade operability and not the service. Collector is the single egress so swapping a backend touches one config file; it re-strips `message.body`, credentials, and auth headers as defence in depth behind FR-056. Loki retains 30 days, deliberately **not** conflated with the audit log's 1-year requirement, which is PostgreSQL's job. **Merged config validated with `docker compose -f docker-compose.yml -f docker-compose.observability.yml config`**

### Audit (Principle IV)

- [X] T047 Create the `audit_event` table and `IAuditLog` implementation in `src/InternalChat.Infrastructure/Persistence/Audit/AuditLog.cs` — `inet` source address, `jsonb` detail, index behind SC-021; an unparseable address never costs the record
- [X] T048 Grant the application role INSERT-only on `audit_event` in `src/InternalChat.Infrastructure/Persistence/Migrations/` — group role `internalchat_app` with SELECT+INSERT only; **TRUNCATE withheld separately from DELETE**; `REVOKE ALL FROM PUBLIC` first
- [X] T049 [P] Integration test asserting the application role cannot UPDATE or DELETE an audit row in `tests/Integration/Audit/AuditImmutabilityTests.cs` — 7 tests **connecting as the restricted role, not the superuser**, asserting SQL state 42501 on UPDATE/DELETE/TRUNCATE while INSERT and SELECT succeed

### Seed and reverse proxy

- [X] T050 [P] Create the seeder tool for development data and load-test volume in `tools/Seeder/Program.cs` — `--dev` seeds 20 employees (the roster T061's Keycloak realm must mirror, joined on `external_subject`), 10 direct conversations, 2 groups, and 400 messages, one group deliberately over the 50-row history page so keyset pagination is exercised by just opening it. All ids are derived rather than random, so re-running appends nothing — **verified: a second run left 20/12/37/400 unchanged**. `--load N` bulk-loads via binary `COPY` at **~80,000 rows/s measured** (125 M ≈ 26 min), spread over the 12-month retention window and calling T026's `internalchat_ensure_month_partitions` first. Bodies come from a skewed bilingual vocabulary because a corpus of identical strings would make the D9 search budget pass while measuring nothing — **verified: 138 k distinct bodies in 200 k rows across 13 partitions, a rare term matching 7,409**. ⚠️ The tables it writes belong to T060/T088, so a schema preflight names the missing table and its owning task instead of failing on raw SQL
- [X] T051 Configure TLS termination and the internal-only attachment location in `deploy/nginx/nginx.conf` — TLS 1.2/1.3, `location /internal-attachments/ { internal; }` reachable only via `X-Accel-Redirect` after the API's membership check (FR-025), range requests passed through for FR-022, `Content-Disposition: attachment` always. Dev certs via `deploy/scripts/generate-dev-certs.sh` (git-ignored)

**Checkpoint**: Foundation ready (T016–T051) — user story implementation can now begin. Solution
builds with zero warnings under `TreatWarningsAsErrors`; 20 architecture, 4 unit, and 42 integration
tests pass, the latter against real PostgreSQL, Redis, RabbitMQ, MinIO, and Keycloak containers.

Two things carried into Phase 3 rather than fixed here:

- The `message` table does not exist yet, so the seeder's insert paths were verified against a
  hand-built stand-in schema, not the real migration. T088 must re-run `--dev` and `--load` once
  the real table lands.
- `body_tsv` as `GENERATED ... to_tsvector('simple', unaccent(body))` **will be rejected**:
  `unaccent` is STABLE, not IMMUTABLE, and a generated column requires IMMUTABLE. T163 needs an
  IMMUTABLE wrapper function. Found while building the stand-in schema.

---

## Phase 3: User Story 1 - Secure Sign-In and Access Control (Priority: P1) 🎯 MVP

**Goal**: Only active employees reach the platform, they see only their own conversations, and
deactivation revokes access within 5 minutes including for an already-open connection.

**Independent Test**: Sign in with corporate credentials and reach a seeded conversation; request a
conversation you do not belong to and be refused; deactivate the employee in Keycloak and confirm
an already-open session stops working within 5 minutes.

### Tests for User Story 1 (REQUIRED) ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [X] T052 [P] [US1] Unit tests for Employee invariants — a deactivated employee cannot be added to a conversation — in `tests/Unit/Domain/EmployeeTests.cs` — 13 tests. Beyond the stated invariant they pin the idempotency directory sync depends on: a redelivered deactivation must not move `deactivated_at`, because that timestamp is what an auditor reads to establish when access ended (SC-021). Also asserts attribute sync can never change `Status` — a rename and a revocation must not be the same operation when only one has a 5-minute budget
- [X] T053 [P] [US1] Unit tests for the authorization evaluator — deny by default, active membership required — in `tests/Unit/Application/ConversationMembershipEvaluatorTests.cs` — 10 tests written around the failure modes rather than the happy path. Includes the two that matter most: a reader failure **refuses** rather than allowing (SC-024 — an outage may cost latency, never a wrong access decision), and `OperationCanceledException` is not swallowed into a denial, which would fill the audit trail with intrusion-shaped noise every time a browser tab closed. A separate test pins `Member < Admin` because the evaluator compares roles with `<`
- [ ] T054 [P] [US1] Contract tests for `/me`, `/me/sessions`, `/directory/employees` against `contracts/openapi.yaml` in `tests/Contract/DirectoryContractTests.cs`
- [ ] T055 [P] [US1] Integration test asserting a refusal is indistinguishable from a not-found in both body and timing in `tests/Integration/Authorization/RefusalOpacityTests.cs`
- [ ] T056 [P] [US1] Integration test asserting deactivation closes an already-open SignalR connection within 5 minutes in `tests/Integration/Authorization/RevocationTests.cs`
- [ ] T057 [P] [US1] Integration test asserting a reconnect with an expired token is refused in `tests/Integration/Authorization/ReconnectValidationTests.cs`

### Implementation for User Story 1

- [X] T058 [P] [US1] Create `Employee`, `EmployeeStatus`, and `Presence` in `src/InternalChat.Domain/Employees/` — the deactivated-employee check lives on the entity (`EnsureCanJoinConversation`) rather than in a use case, so every path that adds a member passes the same gate instead of each new one having to remember. Deactivation and reactivation raise events carrying `external_subject` as well as the internal id, because the revocation set is keyed by the token's `sub` claim and that path has the tightest deadline in the system
- [X] T059 [P] [US1] Create `Membership` with role, `visible_from_seq`, and `removed_at` in `src/InternalChat.Domain/Conversations/Membership.cs` — deliberately not an `Entity<TId>`: the key is the composite `(conversation_id, employee_id)`, and a surrogate id added only to satisfy a base class invites code to reference a membership by something other than the pair that defines it. `Rejoin` takes `Math.Max` of the old and new floor so a re-added member never regains history they were removed from
- [X] T060 [US1] Add EF configurations and a migration for `employee` and `membership` in `src/InternalChat.Infrastructure/Persistence/` — **verified against real PostgreSQL**: `citext` email, GIN trigram on `display_name` for FR-007, and the authorization lookup index made partial (`WHERE removed_at IS NULL`) so a year of membership churn does not come to dominate an index read that happens on every request. No FK to `conversation` yet — that table is T088; the constraint lands there. Two traps found and recorded: declaring the PG enums in *both* `HasPostgresEnum` and `MapEnum` emits duplicate annotations with **conflicting label order** (`membership_role` came out as both `member,admin` and `admin,member` in one migration), and `dotnet ef` with `--no-build` silently uses a stale assembly, writing a migration that does not match the snapshot. ⚠️ PostgreSQL ordered the enum labels alphabetically, so `membership_role` is `admin(1), member(2)` — the reverse of the C# ordering. Harmless today since values store by label, but `ORDER BY role` in SQL would mean the opposite of what it reads like; documented in the migration
- [ ] T061 [US1] Create the Keycloak realm export with 20 development employees, roles, and the SPA client in `deploy/keycloak/realm-export.json`
- [ ] T062 [US1] Configure JWT bearer validation of signature, issuer, audience, and expiry in `src/InternalChat.Api/Program.cs`
- [ ] T063 [US1] Implement the SignalR hub filter validating the token on connect **and** reconnect in `src/InternalChat.Api/Authorization/HubAuthorizationFilter.cs`
- [ ] T064 [US1] Implement the Redis revocation set and the Keycloak back-channel logout endpoint in `src/InternalChat.Infrastructure/Identity/RevocationStore.cs`
- [ ] T065 [US1] Implement the directory sync consumer performing employee upsert and deactivation in `src/InternalChat.Worker/Consumers/DirectorySyncConsumer.cs`
- [X] T066 [US1] Implement `ConversationMembershipRequirement` and its evaluator in `src/InternalChat.Application/Authorization/` — single `Allow` return at the end of the method so a branch added later falls through to the refusal rather than past it, which is the classic authorization bug. An unavailable dependency is a denial (`Undetermined`), never a pass-through. Caching is deliberately not here: `IMembershipReader`'s implementation owns the 30 s cache (T068), leaving the decision a pure function of what the reader returns
- [ ] T067 [US1] Implement the endpoint filter applying the membership policy in `src/InternalChat.Api/Authorization/MembershipEndpointFilter.cs`
- [ ] T068 [US1] Implement the 30-second membership cache with an explicit invalidation contract in `src/InternalChat.Infrastructure/Caching/MembershipCache.cs`
- [ ] T069 [US1] Implement `GET /me` including `canReceiveNotifications` in `src/InternalChat.Api/Endpoints/MeEndpoints.cs`
- [ ] T070 [US1] Implement `GET` and `DELETE /me/sessions` in `src/InternalChat.Api/Endpoints/MeEndpoints.cs`
- [ ] T071 [US1] Implement `GET /directory/employees` with a trigram index in `src/InternalChat.Api/Endpoints/DirectoryEndpoints.cs`
- [ ] T072 [US1] Emit audit events for sign-in, sign-out, and access denial in `src/InternalChat.Application/Behaviors/AuditBehavior.cs`
- [ ] T073 [P] [US1] Implement the OIDC authorization-code-with-PKCE flow in `src/internalchat-web/src/lib/auth/`
- [ ] T074 [P] [US1] Implement protected routes and silent token refresh in `src/internalchat-web/src/lib/auth/TokenRefresh.ts`
- [ ] T075 [P] [US1] Vitest tests for the auth library in `src/internalchat-web/tests/auth.test.ts`
- [ ] T076 [US1] Playwright E2E covering quickstart V1 in `tests/e2e/v1-signin-revocation.spec.ts`

**Checkpoint**: Authentication and authorization work end to end. Nothing else may be built until
the refusal-opacity and revocation tests pass.

---

## Phase 4: User Story 2 - One-to-One Direct Messaging (Priority: P1) 🎯 MVP

**Goal**: Two employees exchange messages in real time, exactly once, in a stable order, with
history that loads progressively and survives reconnects.

**Independent Test**: Two browsers, one direct conversation; every message appears on the other
side without a refresh; a retried send produces exactly one message; three messages sent across a
network drop all arrive once, in order.

### Tests for User Story 2 (REQUIRED) ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T077 [P] [US2] Unit tests for message body length, required ULID key, and the 24-hour edit/delete window in `tests/Unit/Domain/MessageTests.cs`
- [ ] T078 [P] [US2] Unit tests asserting ordering is derived from the server sequence and never from a client clock in `tests/Unit/Domain/MessageOrderingTests.cs`
- [ ] T079 [P] [US2] Unit tests for direct-conversation invariants — exactly two members, neither removable — in `tests/Unit/Domain/ConversationTests.cs`
- [ ] T080 [P] [US2] Contract tests for the conversation and message endpoints against `contracts/openapi.yaml` in `tests/Contract/MessagingContractTests.cs`
- [ ] T081 [P] [US2] Contract tests asserting SignalR events match `contracts/signalr-hub.md` in `tests/Contract/HubContractTests.cs`
- [ ] T082 [P] [US2] Integration test asserting a duplicate `clientMessageKey` returns 200 and leaves exactly one row in `tests/Integration/Messages/IdempotentSendTests.cs`
- [ ] T083 [P] [US2] Integration test asserting keyset pagination is correct across a monthly partition boundary in `tests/Integration/Messages/HistoryPaginationTests.cs`
- [ ] T084 [P] [US2] Integration test asserting `Resync` returns everything above `lastSeenSeq` exactly once in `tests/Integration/Messages/ResyncTests.cs`
- [ ] T085 [P] [US2] Integration test asserting two concurrent direct-conversation creations produce one conversation in `tests/Integration/Conversations/DirectDedupeTests.cs`

### Implementation for User Story 2

- [ ] T086 [P] [US2] Create `Message`, `MessageBody`, `ClientMessageKey`, and `EditWindow` in `src/InternalChat.Domain/Messages/`
- [ ] T087 [P] [US2] Create `Conversation` with `direct_key`, `last_seq`, and `HistoryVisibility` in `src/InternalChat.Domain/Conversations/Conversation.cs`
- [ ] T088 [US2] Add EF configurations and a migration for `conversation` and partitioned `message`, including the unique indexes on `(conversation_id, client_message_key)` and `(conversation_id, seq)` in `src/InternalChat.Infrastructure/Persistence/`
- [ ] T089 [US2] Implement the monthly partition creation job in `src/InternalChat.Worker/Jobs/PartitionMaintenanceJob.cs`
- [ ] T090 [US2] Implement `IMessageRepository` and `IConversationRepository` in `src/InternalChat.Infrastructure/Persistence/Repositories/`
- [ ] T091 [US2] Implement the `CreateConversation` use case with direct deduplication via `direct_key` in `src/InternalChat.Application/Conversations/CreateConversation.cs`
- [ ] T092 [US2] Implement the `SendMessage` use case — sequence allocation, message insert, and outbox write in one transaction, returning the existing message on key collision — in `src/InternalChat.Application/Messages/SendMessage.cs`
- [ ] T093 [US2] Implement the `GetHistory` use case with keyset pagination and the `visible_from_seq` floor in `src/InternalChat.Application/Messages/GetHistory.cs`
- [ ] T094 [US2] Implement the `EditMessage` and `DeleteMessage` use cases enforcing the 24-hour window in `src/InternalChat.Application/Messages/`
- [ ] T095 [US2] Implement the conversation endpoints in `src/InternalChat.Api/Endpoints/ConversationEndpoints.cs`
- [ ] T096 [US2] Implement the message send, history, edit, and delete endpoints in `src/InternalChat.Api/Endpoints/MessageEndpoints.cs`
- [ ] T097 [US2] Implement `ChatHub` with `Resync`, `StartTyping`, `StopTyping`, and `SetPresence`, delegating every method to a use case, in `src/InternalChat.Api/Hubs/ChatHub.cs`
- [ ] T098 [US2] Implement the real-time fan-out consumer emitting `MessageReceived`, `MessageEdited`, and `MessageDeleted` in `src/InternalChat.Api/Consumers/RealtimeFanoutConsumer.cs`
- [ ] T099 [US2] Implement presence and typing Redis keys with TTL expiry rather than explicit cleanup in `src/InternalChat.Infrastructure/Caching/PresenceStore.cs`
- [ ] T100 [P] [US2] Build the conversation list with TanStack Query in `src/internalchat-web/src/features/conversations/`
- [ ] T101 [P] [US2] Build the virtualized message list with `react-virtuoso` in `src/internalchat-web/src/features/messages/MessageList.tsx`
- [ ] T102 [P] [US2] Build the composer with optimistic send and an offline queue keyed by ULID in `src/internalchat-web/src/features/messages/Composer.tsx`
- [ ] T103 [P] [US2] Build the SignalR client with automatic reconnect and `Resync` in `src/internalchat-web/src/lib/realtime/`
- [ ] T104 [P] [US2] Build typing indicators and presence display in `src/internalchat-web/src/features/messages/TypingIndicator.tsx`
- [ ] T105 [P] [US2] Vitest tests asserting the offline queue never sends a duplicate after reconnect in `src/internalchat-web/tests/offline-queue.test.ts`
- [ ] T106 [US2] Playwright two-client delivery timing harness measuring SC-006 in `tests/e2e/v2-delivery-timing.spec.ts`
- [ ] T107 [US2] k6 script for the send and history budgets in `tests/Load/messaging.js`
- [ ] T108 [US2] Verify budgets against a database seeded to full retention volume, not an empty one, in `tests/Load/README.md`

**Checkpoint**: **MVP complete.** Employees can sign in and exchange direct messages reliably.
Deployable and demonstrable on its own.

---

## Phase 5: User Story 3 - Group Conversations (Priority: P2)

**Goal**: Teams converse in named groups with membership that changes over time and a clear,
enforced history-visibility rule.

**Independent Test**: Create a group with three members, post, add a fourth and confirm their
history floor, remove one and confirm they stop receiving immediately.

### Tests for User Story 3 (REQUIRED) ⚠️

- [ ] T109 [P] [US3] Unit tests for group name requirement and the rule that a re-add never lowers `visible_from_seq` in `tests/Unit/Domain/MembershipTests.cs`
- [ ] T110 [P] [US3] Contract tests for the membership endpoints in `tests/Contract/MembershipContractTests.cs`
- [ ] T111 [P] [US3] Integration test asserting a removed member immediately loses new messages and attachments in `tests/Integration/Conversations/RemovalTests.cs`
- [ ] T112 [P] [US3] Integration test asserting a newly added member sees history only from their join point in `tests/Integration/Conversations/HistoryVisibilityTests.cs`
- [ ] T113 [P] [US3] Integration test asserting delivery to a 500-member group stays inside the budget in `tests/Integration/Conversations/LargeGroupDeliveryTests.cs`

### Implementation for User Story 3

- [ ] T114 [US3] Implement the `AddMember` and `RemoveMember` use cases with membership cache invalidation in `src/InternalChat.Application/Conversations/`
- [ ] T115 [US3] Implement the membership endpoints in `src/InternalChat.Api/Endpoints/MembershipEndpoints.cs`
- [ ] T116 [US3] Publish `chat.membership.changed.v1` and consume it to move SignalR group assignment immediately in `src/InternalChat.Api/Consumers/MembershipChangeConsumer.cs`
- [ ] T117 [US3] Implement mention resolution restricted to active members at send time in `src/InternalChat.Application/Messages/MentionResolver.cs`
- [ ] T118 [US3] Route fan-out for conversations above 1,000 members through the Worker so the sender's accept budget is independent of member count in `src/InternalChat.Worker/Consumers/LargeConversationFanoutConsumer.cs`
- [ ] T119 [P] [US3] Build group creation and member management UI in `src/internalchat-web/src/features/conversations/GroupSettings.tsx`
- [ ] T120 [P] [US3] Build mention autocomplete in `src/internalchat-web/src/features/messages/MentionAutocomplete.tsx`
- [ ] T121 [P] [US3] Display the active history-visibility rule to members in `src/internalchat-web/src/features/conversations/HistoryNotice.tsx`
- [ ] T122 [US3] Playwright E2E covering quickstart V3 in `tests/e2e/v3-groups.spec.ts`

**Checkpoint**: User Stories 1–3 all work independently.

---

## Phase 6: User Story 4 - Notifications and Unread State (Priority: P2)

**Goal**: Employees are reached when away, without being flooded, with unread state consistent
across every device — and are told plainly when they cannot be reached.

**Independent Test**: Two devices; a background DM produces a notification within 5 seconds;
reading on one device clears the badge on the other; denying permission produces a visible warning.

### Tests for User Story 4 (REQUIRED) ⚠️

- [ ] T123 [P] [US4] Unit tests for do-not-disturb evaluation across time zones in `tests/Unit/Domain/DoNotDisturbTests.cs`
- [ ] T124 [P] [US4] Unit tests asserting read state merges monotonically and never moves backwards in `tests/Unit/Domain/ReadStateTests.cs`
- [ ] T125 [P] [US4] Contract tests for the notification endpoints in `tests/Contract/NotificationContractTests.cs`
- [ ] T126 [P] [US4] Integration test asserting unread state clears across a second device in `tests/Integration/Notifications/CrossDeviceReadTests.cs`
- [ ] T127 [P] [US4] Integration test asserting no notification for an ordinary group message but one for a mention in `tests/Integration/Notifications/MentionOnlyTests.cs`
- [ ] T128 [P] [US4] Integration test asserting a rejected push subscription is deleted rather than retried in `tests/Integration/Notifications/SubscriptionPruningTests.cs`

### Implementation for User Story 4

- [ ] T129 [P] [US4] Create `ReadState`, `NotificationPreference`, `DoNotDisturbWindow`, and `MuteSetting` in `src/InternalChat.Domain/Notifications/`
- [ ] T130 [US4] Add EF configurations and a migration for `read_state`, `notification_preference`, and `push_subscription` in `src/InternalChat.Infrastructure/Persistence/`
- [ ] T131 [US4] Implement the `MarkRead` use case with a monotonic merge in `src/InternalChat.Application/Notifications/MarkRead.cs`
- [ ] T132 [US4] Implement the `UpdatePreferences` use case in `src/InternalChat.Application/Notifications/UpdatePreferences.cs`
- [ ] T133 [US4] Implement the notification fan-out consumer honouring mute, do-not-disturb, and mention-only rules in `src/InternalChat.Worker/Consumers/NotificationFanoutConsumer.cs`
- [ ] T134 [US4] Implement `IPushSender` over VAPID web push in `src/InternalChat.Infrastructure/Push/WebPushSender.cs`
- [ ] T135 [US4] Implement the backlog digest job for returning employees in `src/InternalChat.Worker/Jobs/DigestJob.cs`
- [ ] T136 [US4] Implement the read-state, preference, and subscription endpoints in `src/InternalChat.Api/Endpoints/NotificationEndpoints.cs`
- [ ] T137 [US4] Broadcast `ReadStateUpdated` to the employee's other devices in `src/InternalChat.Api/Consumers/ReadStateConsumer.cs`
- [ ] T138 [P] [US4] Build the service worker handling push and nothing else in `src/internalchat-web/src/lib/push/service-worker.ts`
- [ ] T139 [P] [US4] Build notification capability detection and the plain-language warning required by FR-040 in `src/internalchat-web/src/features/notifications/NotificationCapability.tsx`
- [ ] T140 [P] [US4] Build unread badges and the do-not-disturb settings UI in `src/internalchat-web/src/features/notifications/`
- [ ] T141 [US4] Playwright E2E covering quickstart V4 in `tests/e2e/v4-notifications.spec.ts`

**Checkpoint**: The platform is now a reliable channel rather than somewhere to check.

---

## Phase 7: User Story 5 - Image Sharing (Priority: P2)

**Goal**: Screenshots and photos post inline, are scanned before anyone can retrieve them, and obey
the same access rules as the conversation.

**Independent Test**: Post an image to a group; every member sees it inline; a non-member with the
content URL is refused; EICAR never becomes retrievable.

### Tests for User Story 5 (REQUIRED) ⚠️

- [ ] T142 [P] [US5] Unit tests for per-kind size and content-type constraints in `tests/Unit/Domain/FileConstraintsTests.cs`
- [ ] T143 [P] [US5] Contract tests for the attachment endpoints in `tests/Contract/AttachmentContractTests.cs`
- [ ] T144 [P] [US5] Integration test asserting a non-member is refused the content URL in `tests/Integration/Attachments/AccessControlTests.cs`
- [ ] T145 [P] [US5] Integration test asserting the EICAR test file never becomes retrievable in `tests/Integration/Attachments/MalwareScanTests.cs`
- [ ] T146 [P] [US5] Integration test asserting deleting a message stops the attachment being served in `tests/Integration/Attachments/DeletionTests.cs`
- [ ] T147 [P] [US5] Integration test asserting an interrupted upload leaves no partial or duplicate attachment in `tests/Integration/Attachments/InterruptedUploadTests.cs`

### Implementation for User Story 5

- [ ] T148 [P] [US5] Create `Attachment`, `ScanVerdict`, and `FileConstraints` in `src/InternalChat.Domain/Attachments/`
- [ ] T149 [US5] Add the EF configuration and migration for `attachment` with the denormalized `conversation_id` in `src/InternalChat.Infrastructure/Persistence/`
- [ ] T150 [US5] Implement `IObjectStore` over MinIO with quarantine and clean buckets in `src/InternalChat.Infrastructure/Storage/MinioObjectStore.cs`
- [ ] T151 [US5] Implement the `RequestUpload` use case validating kind, type, size, and duration before any bytes transfer in `src/InternalChat.Application/Attachments/RequestUpload.cs`
- [ ] T152 [US5] Implement the ClamAV scan consumer performing promotion and emitting `AttachmentReady` in `src/InternalChat.Worker/Consumers/AttachmentScanConsumer.cs`
- [ ] T153 [US5] Implement the `AuthorizeDownload` use case and the `X-Accel-Redirect` handoff so bytes never pass through .NET in `src/InternalChat.Api/Endpoints/AttachmentEndpoints.cs`
- [ ] T154 [US5] Configure the internal-only attachment location with range support in `deploy/nginx/nginx.conf`
- [ ] T155 [US5] Implement the storage capacity monitor and administrator alert in `src/InternalChat.Worker/Jobs/StorageCapacityJob.cs`
- [ ] T156 [P] [US5] Build image upload with progress and clipboard paste in `src/internalchat-web/src/features/attachments/ImageUpload.tsx`
- [ ] T157 [P] [US5] Build the inline preview and lightbox in `src/internalchat-web/src/features/attachments/ImagePreview.tsx`
- [ ] T158 [US5] Playwright E2E covering quickstart V5 image scenarios in `tests/e2e/v5-images.spec.ts`

**Checkpoint**: Attachments work with the access model intact.

---

## Phase 8: User Story 6 - Search Across Messages and Files (Priority: P3)

**Goal**: Employees find messages across their own conversations quickly, and never learn that
content exists in conversations they cannot access.

**Independent Test**: Search a phrase and get ranked results only from your conversations; search a
phrase that exists only elsewhere and get nothing, with nothing in timing or counts revealing it.

### Tests for User Story 6 (REQUIRED) ⚠️

- [ ] T159 [P] [US6] Unit tests for query parsing and filter validation in `tests/Unit/Application/SearchQueryTests.cs`
- [ ] T160 [P] [US6] Contract tests for the search endpoint in `tests/Contract/SearchContractTests.cs`
- [ ] T161 [P] [US6] Integration test asserting no results and indistinguishable timing for content in non-member conversations in `tests/Integration/Search/AccessScopingTests.cs`
- [ ] T162 [P] [US6] Integration test asserting edits, deletions, and membership removal are reflected in results in `tests/Integration/Search/ReindexTests.cs`

### Implementation for User Story 6

- [ ] T163 [US6] Add the `unaccent` extension, the generated `body_tsv` column, and the GIN index in `src/InternalChat.Infrastructure/Persistence/Migrations/`
- [ ] T164 [US6] Implement `ISearchIndex` over PostgreSQL full-text search with a membership pre-filter applied before ranking in `src/InternalChat.Infrastructure/Search/PostgresSearchIndex.cs`
- [ ] T165 [US6] Implement the `SearchMessages` use case with filters and an explicit truncation flag in `src/InternalChat.Application/Search/SearchMessages.cs`
- [ ] T166 [US6] Implement the search endpoint in `src/InternalChat.Api/Endpoints/SearchEndpoints.cs`
- [ ] T167 [US6] Implement the search index consumer handling edits, deletions, and membership changes in `src/InternalChat.Worker/Consumers/SearchIndexConsumer.cs`
- [ ] T168 [P] [US6] Build the search UI with filters and jump-to-context in `src/internalchat-web/src/features/search/`
- [ ] T169 [US6] Seed a 125-million-message corpus and verify the search budget — this is the D9 decision trigger for OpenSearch — in `tests/Load/search.js`
- [ ] T170 [US6] Playwright E2E covering quickstart V6 in `tests/e2e/v6-search.spec.ts`

**Checkpoint**: History becomes an asset rather than a liability.

---

## Phase 9: User Story 7 - Video File Sharing (Priority: P3)

**Goal**: Short recordings play in place without downloading the whole file, under the same access
rules and without transcoding on the application host.

**Independent Test**: Upload a valid MP4 and confirm playback starts before full download; upload a
600 MB file and confirm rejection before any bytes transfer.

### Tests for User Story 7 (REQUIRED) ⚠️

- [ ] T171 [P] [US7] Unit tests for video duration, size, and codec allow-list in `tests/Unit/Domain/VideoConstraintsTests.cs`
- [ ] T172 [P] [US7] Integration test asserting a range request returns 206 with correct bytes in `tests/Integration/Attachments/RangeRequestTests.cs`
- [ ] T173 [P] [US7] Integration test asserting an oversized or unsupported video is rejected before upload with the limit stated in `tests/Integration/Attachments/VideoRejectionTests.cs`

### Implementation for User Story 7

- [ ] T174 [US7] Extend `FileConstraints` with video duration and the browser-playable codec allow-list in `src/InternalChat.Domain/Attachments/FileConstraints.cs`
- [ ] T175 [US7] Add poster-frame extraction and `moov` atom relocation to the scan consumer in `src/InternalChat.Worker/Consumers/AttachmentScanConsumer.cs`
- [ ] T176 [US7] Enable range-request support through the internal redirect in `deploy/nginx/nginx.conf`
- [ ] T177 [P] [US7] Build the video player with poster and seek in `src/internalchat-web/src/features/attachments/VideoPlayer.tsx`
- [ ] T178 [US7] Playwright E2E covering quickstart V7 video scenarios in `tests/e2e/v7-video.spec.ts`

**Checkpoint**: All non-meeting functionality is complete and deployable on a single host.

---

## Phase 10: User Story 8 - Video Meetings (Priority: P4)

**Goal**: Up to 25 participants meet from a conversation, with the platform-wide ceiling enforced
by refusal rather than by degradation.

**Independent Test**: Three participants join from separate machines with two-way audio and video;
a non-member is refused a join token; at the ceiling, new meetings are refused while running
meetings are unaffected.

> **Gate**: do not start this phase until the application host meets its messaging budgets. Meeting
> work must never be able to mask a messaging regression.

### Tests for User Story 8 (REQUIRED) ⚠️

- [ ] T179 [P] [US8] Unit tests for the 25-participant cap, the 1,250 platform ceiling, and refusal behaviour in `tests/Unit/Domain/MeetingCapacityTests.cs`
- [ ] T180 [P] [US8] Contract tests for the meeting endpoints in `tests/Contract/MeetingContractTests.cs`
- [ ] T181 [P] [US8] Integration test asserting a non-member is refused a join token in `tests/Integration/Meetings/TokenAuthorizationTests.cs`
- [ ] T182 [P] [US8] Integration test asserting that with the media host unreachable, meetings report unavailable while messaging, attachments, search, and notifications keep working in `tests/Integration/Meetings/MediaHostDegradationTests.cs`

### Implementation for User Story 8

- [ ] T183 [P] [US8] Create `Meeting`, `Participation`, and `ShareSession` in `src/InternalChat.Domain/Meetings/`
- [ ] T184 [US8] Add EF configurations and a migration for `meeting` and `participation` in `src/InternalChat.Infrastructure/Persistence/`
- [ ] T185 [US8] Create the media host stack with LiveKit and TURN in `deploy/docker-compose.media.yml`
- [ ] T186 [US8] Implement `IMeetingTokenIssuer` over LiveKit in `src/InternalChat.Infrastructure/Meetings/LiveKitTokenIssuer.cs`
- [ ] T187 [US8] Implement the `StartMeeting` use case with the Redis platform-wide capacity guard in `src/InternalChat.Application/Meetings/StartMeeting.cs`
- [ ] T188 [US8] Implement the `IssueJoinToken` use case performing the membership check that IS the meeting access control in `src/InternalChat.Application/Meetings/IssueJoinToken.cs`
- [ ] T189 [US8] Implement the signature-verified LiveKit webhook receiver driving meeting lifecycle in `src/InternalChat.Api/Endpoints/MeetingWebhookEndpoints.cs`
- [ ] T190 [US8] Implement the meeting endpoints including the 503 with `retryAfterSeconds` at capacity in `src/InternalChat.Api/Endpoints/MeetingEndpoints.cs`
- [ ] T191 [US8] Emit `MeetingStarted` and `MeetingEnded` over SignalR in `src/InternalChat.Api/Consumers/MeetingLifecycleConsumer.cs`
- [ ] T192 [US8] Emit audit events for meeting start, join, leave, and end in `src/InternalChat.Worker/Consumers/MeetingAuditConsumer.cs`
- [ ] T193 [P] [US8] Build the lazy-loaded meetings feature so the LiveKit SDK stays out of the initial bundle in `src/internalchat-web/src/features/meetings/`
- [ ] T194 [P] [US8] Build the join prompt, participant tiles, and mute and camera controls in `src/internalchat-web/src/features/meetings/MeetingRoom.tsx`
- [ ] T195 [US8] Add a bundle-size assertion confirming meetings are excluded from the initial chunk in `src/internalchat-web/vite.config.ts`
- [ ] T196 [US8] k6 load test to 25 participants per room and the 1,250 platform ceiling in `tests/Load/meetings.js`
- [ ] T197 [US8] Measure actual media host capacity on real hardware — the research D12 open item — and record it in `specs/001-enterprise-chat-platform/research.md`
- [ ] T198 [US8] Playwright E2E covering quickstart V7 meeting scenarios in `tests/e2e/v8-meetings.spec.ts`

**Checkpoint**: Meetings work without affecting messaging.

---

## Phase 11: User Story 9 - Screen Sharing in Meetings (Priority: P4)

**Goal**: A participant shares a screen or a single window legibly, with one defined rule when two
people share at once, and no leakage of other applications.

**Independent Test**: Share a single window, switch applications, and confirm the other application
is not revealed; start a second share and confirm the defined rule applies visibly.

### Tests for User Story 9 (REQUIRED) ⚠️

- [ ] T199 [P] [US9] Unit tests for the single-publisher screen-share rule in `tests/Unit/Domain/ShareSessionTests.cs`
- [ ] T200 [P] [US9] Integration test asserting a second simultaneous share request is handled by the defined rule rather than left ambiguous in `tests/Integration/Meetings/ConcurrentShareTests.cs`

### Implementation for User Story 9

- [ ] T201 [US9] Enforce the single screen-share publisher server-side in `src/InternalChat.Application/Meetings/StartScreenShare.cs`
- [ ] T202 [US9] Track `shared_screen_seconds` per participation in `src/InternalChat.Worker/Consumers/MeetingLifecycleConsumer.cs`
- [ ] T203 [P] [US9] Build the share control with a full-screen versus single-window picker in `src/internalchat-web/src/features/meetings/ScreenShareControl.tsx`
- [ ] T204 [P] [US9] Build the enlarge-shared-view control and the "you are viewing X's share" indicator in `src/internalchat-web/src/features/meetings/SharedView.tsx`
- [ ] T205 [US9] Playwright E2E covering quickstart V7 screen-share scenarios in `tests/e2e/v9-screenshare.spec.ts`

**Checkpoint**: All nine user stories are independently functional.

---

## Phase 12: Polish, Retention & Cross-Cutting Concerns

**Purpose**: Operational obligations and platform-wide verification

> **Retention is not optional polish.** FR-052 through FR-055 are compliance requirements and the
> sweep must exist and be verified before first production use, even though its effect is not
> visible for 12 months. It sits here only because it depends on every entity existing first.

- [ ] T206 Implement the retention sweep using partition drops with per-batch audit events in `src/InternalChat.Worker/Jobs/RetentionSweepJob.cs`
- [ ] T207 Implement the retention policy endpoints with audited changes in `src/InternalChat.Api/Endpoints/AdminEndpoints.cs`
- [ ] T208 [P] Display the active retention period to every employee in `src/internalchat-web/src/features/settings/RetentionNotice.tsx`
- [ ] T209 Implement the export job with audit recording in `src/InternalChat.Worker/Jobs/ExportJob.cs`
- [ ] T210 Implement the orphaned object reclamation sweep in `src/InternalChat.Worker/Jobs/OrphanReclaimJob.cs`
- [ ] T211 Integration test asserting the retention tolerance — nothing deleted early, nothing surviving more than 7 days past — in `tests/Integration/Retention/RetentionToleranceTests.cs`
- [ ] T212 Create scripted PostgreSQL and MinIO backup with a rehearsed restore in `deploy/scripts/backup.sh` and `deploy/scripts/restore.sh`
- [ ] T213 Integration test asserting a full cache-tier outage causes latency only, never data loss or an incorrect access decision, in `tests/Integration/Resilience/CacheOutageTests.cs`
- [ ] T214 [P] Security verification pass over authorization policies, rate limits, headers, CSP, and audit coverage in `docs/security-review.md`
- [ ] T215 [P] Accessibility pass to WCAG 2.1 AA on interactive components in `src/internalchat-web/tests/a11y.test.ts`
- [ ] T216 [P] Configure the Lighthouse CI bundle and Core Web Vitals budget in `.github/workflows/ci.yml`
- [ ] T217 Verify all per-layer coverage floors are met and enforced in `.github/workflows/ci.yml`
- [ ] T218 Run the full messaging-path load test against seeded production-volume data in `tests/Load/`
- [ ] T219 Execute the complete quickstart validation V1 through V9 in `specs/001-enterprise-chat-platform/quickstart.md`
- [ ] T220 [P] Write the operations runbook covering deployment, rollback, backup, and DLQ handling in `docs/runbook.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies — start immediately
- **Foundational (Phase 2)**: Depends on Setup — **BLOCKS all user stories**
- **US1 (Phase 3)**: Depends on Foundational. Blocks every other story, because nothing can be
  authorized until the authorization pipeline exists.
- **US2 (Phase 4)**: Depends on US1
- **US3 (Phase 5)**: Depends on US2 — reuses its delivery mechanics
- **US4 (Phase 6)**: Depends on US2; most valuable after US3
- **US5 (Phase 7)**: Depends on US2
- **US6 (Phase 8)**: Depends on US2; needs US3 for meaningful membership scoping
- **US7 (Phase 9)**: Depends on US5 — extends the same attachment pipeline
- **US8 (Phase 10)**: Depends on US2. **Additionally gated** on the application host meeting its
  messaging budgets.
- **US9 (Phase 11)**: Depends on US8 — meaningless without it
- **Polish (Phase 12)**: Depends on all entities existing

### Genuine parallelism after Phase 4

Once US2 is complete, three tracks can run concurrently with different people:

| Track | Stories | Overlap risk |
| --- | --- | --- |
| A | US3 → US6 | Touches conversation and search paths |
| B | US4 | Touches notification and read-state paths only |
| C | US5 → US7 | Touches attachment and storage paths only |

US8 and US9 are a fourth track but should not start until Track A, B, or C has proven the
messaging budgets hold under load.

### Within Each User Story

- Tests MUST be written and observed FAILING before implementation (Principle III)
- Domain before Application; Application before Infrastructure; Infrastructure before Presentation
- Story complete before moving to the next priority

---

## Parallel Example: User Story 2

```bash
# Launch all tests for User Story 2 together (write them FIRST, confirm they fail):
Task: "Unit tests for message body length and edit window in tests/Unit/Domain/MessageTests.cs"
Task: "Unit tests for server-sequence ordering in tests/Unit/Domain/MessageOrderingTests.cs"
Task: "Contract tests for message endpoints in tests/Contract/MessagingContractTests.cs"
Task: "Integration test for duplicate clientMessageKey in tests/Integration/Messages/IdempotentSendTests.cs"

# Launch both domain entities together:
Task: "Create Message aggregate in src/InternalChat.Domain/Messages/Message.cs"
Task: "Create Conversation aggregate in src/InternalChat.Domain/Conversations/Conversation.cs"

# Launch all frontend work together once the API contract is stable:
Task: "Conversation list in src/internalchat-web/src/features/conversations/"
Task: "Virtualized message list in src/internalchat-web/src/features/messages/MessageList.tsx"
Task: "Composer with offline queue in src/internalchat-web/src/features/messages/Composer.tsx"
Task: "SignalR client with Resync in src/internalchat-web/src/lib/realtime/"
```

---

## Implementation Strategy

### MVP First (US1 + US2)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational — **critical, blocks everything**
3. Complete Phase 3: US1 — authentication and authorization
4. Complete Phase 4: US2 — direct messaging
5. **STOP and VALIDATE**: run quickstart V1 and V2, and measure the performance budget against
   seeded full-volume data
6. Deploy and demonstrate

At this point 10,000 employees can sign in and message each other reliably. Everything after is
additive.

### Incremental Delivery

1. Setup + Foundational → foundation ready
2. US1 + US2 → **MVP**, deployable
3. US3 → team conversation
4. US4 → the platform becomes a reliable channel
5. US5 → screenshots, the most common attachment
6. US6 → history becomes searchable
7. US7 → video files
8. US8 + US9 → meetings, requiring the second host

Each increment is independently deployable and does not break the previous one.

### Constitution Gates at Every Increment

No increment is done until: tests were written first and pass; coverage floors hold; the declared
performance budget is measured and met; security controls for the touched surface are verified;
structured logs, metrics, and traces cover new paths; migrations apply forward cleanly; and the
OpenAPI document and message contracts are updated.

---

## Notes

- [P] tasks touch different files and have no incomplete dependencies
- [Story] labels map tasks to spec.md user stories for traceability
- Verify tests fail before implementing — NON-NEGOTIABLE per Constitution Principle III
- Every new endpoint, hub method, and consumer needs an explicit authorization policy task; the
  architecture test at T019 fails the build if one is missing
- Every cache write task needs a matching invalidation task and integration test
- Every RabbitMQ consumer task must state its idempotency key
- Commit after each task or logical group
- Stop at any checkpoint to validate a story independently
