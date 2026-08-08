<!--
SYNC IMPACT REPORT
==================
Version change: [CONSTITUTION_VERSION] (unfilled template) -> 1.0.0
Bump rationale: Initial ratification. Template placeholders replaced with concrete,
enforceable engineering principles for the InternalChat platform.

Modified principles (template slot -> concrete principle):
  [PRINCIPLE_1_NAME] -> I. Clean Architecture & The Dependency Rule
  [PRINCIPLE_2_NAME] -> II. SOLID by Default
  [PRINCIPLE_3_NAME] -> III. Test-First & Unit Test Coverage (NON-NEGOTIABLE)
  [PRINCIPLE_4_NAME] -> IV. Security by Default
  [PRINCIPLE_5_NAME] -> V. Performance & Scalability Budgets
  (added)            -> VI. Asynchronous Messaging Contracts
  (added)            -> VII. Data Authority & Cache Discipline

Added sections:
  - Technology Stack & Platform Constraints (was [SECTION_2_NAME])
  - Security Requirements (new)
  - Performance Requirements (new)
  - Development Workflow & Quality Gates (was [SECTION_3_NAME])

Removed sections: none

Templates requiring updates:
  ✅ .specify/templates/plan-template.md    - Constitution Check gates populated
  ✅ .specify/templates/tasks-template.md   - unit tests changed from OPTIONAL to REQUIRED
  ✅ .specify/templates/spec-template.md    - security/performance success criteria guidance added
  ✅ .specify/extensions/agent-context/commands/speckit.agent-context.update.md - reviewed, no
     outdated principle references
  ⚠ README.md / docs/quickstart.md          - do not exist yet; create during first feature plan

Follow-up TODOs: none. RATIFICATION_DATE set to initial adoption date 2026-07-31.

---
AMENDMENT 2026-07-31: 1.0.0 -> 1.1.0
Bump rationale: MINOR. A new principle and a new mandatory section were added; no existing
principle was removed or redefined incompatibly.

Added:
  - Principle VIII. Zero-Cost, Self-Hosted, Open Source Only
  - Section: Local Development & Deployment (Docker-First)

Materially expanded:
  - Technology Stack table: every row now names a free, self-hostable implementation
  - Security Requirements / Supply Chain: paid scanners replaced with named free tooling
  - Performance Requirements / Scale and Capacity: single-host Compose caveat made explicit

Templates requiring updates:
  ✅ .specify/templates/plan-template.md - gate row VIII added
  ⚠ docker-compose.yml, Dockerfiles, .env.example - do not exist yet; create in first feature plan

---
AMENDMENT 2026-07-31: 1.1.0 -> 1.2.0
Bump rationale: MINOR. Deployment guidance materially expanded with a single, narrowly scoped
exception. No principle removed or redefined; the zero-cost and Docker-first rules are unchanged.

Driver: specs/001-enterprise-chat-platform requires 25 participants per meeting and up to 1,250
concurrent meeting participants platform-wide. Real-time media relay at that level cannot share a
host with the messaging workload without letting meetings degrade messaging for everyone.

Modified:
  - Local Development & Deployment / Deployment Strategy: one additional Linux host MAY be
    dedicated to media relay; the application MUST stay functional without it
  - Performance Requirements / Scale and Capacity: meeting capacity budget added

Templates requiring updates:
  ✅ .specify/templates/plan-template.md - Docker-First gate row updated for the media-host carve-out
-->

# InternalChat Constitution

## Core Principles

### I. Clean Architecture & The Dependency Rule

The solution MUST be organized into four concentric layers, and source-code dependencies MUST
point inward only:

- **Domain** (`InternalChat.Domain`): entities, value objects, domain events, domain services,
  and invariants. It MUST NOT reference any other project, ASP.NET Core, EF Core, Redis,
  RabbitMQ, or any NuGet package other than the BCL.
- **Application** (`InternalChat.Application`): use cases, orchestration, authorization
  decisions, and the *interfaces* for all outbound concerns (`IMessageRepository`,
  `ICacheStore`, `IEventPublisher`, `IClock`). It MUST depend only on Domain.
- **Infrastructure** (`InternalChat.Infrastructure`): EF Core/PostgreSQL, Redis, RabbitMQ,
  identity providers, file storage. It implements Application interfaces and MUST NOT be
  referenced by Domain or Application.
- **Presentation** (`InternalChat.Api`, `internalchat-web`): HTTP endpoints, SignalR hubs, DTOs,
  React components. It MUST depend on Application, never on Infrastructure types, and never on
  Domain entities in its public contracts.

Additional non-negotiables:

- Infrastructure is wired into the process exclusively through dependency injection at the
  composition root (`Program.cs`). No other file may construct an Infrastructure type directly.
- Domain entities MUST NOT be serialized to HTTP or message-bus payloads. Every boundary
  crossing uses an explicit DTO or contract type.
- Business rules MUST live in Domain or Application. Controllers, hubs, React components, and
  EF Core configurations containing business rules are a build-blocking review defect.

*Rationale*: A chat platform's messaging, membership, and retention rules outlive any framework
version. Isolating them from ASP.NET Core, PostgreSQL, and the current React generation is what
makes a decade-long internal system maintainable and testable without infrastructure.

### II. SOLID by Default

Every type submitted MUST satisfy the following, and reviewers MUST reject violations:

- **Single Responsibility**: one reason to change per class. Use-case handlers do one use case.
  A class exceeding 300 lines or a method exceeding 50 lines MUST be justified in the PR
  description or split.
- **Open/Closed**: extend behavior through new implementations, strategies, or pipeline
  behaviors — not by adding branches to existing switch/if chains over type or role codes.
- **Liskov Substitution**: an implementation MUST NOT strengthen preconditions, weaken
  postconditions, or throw `NotSupportedException` for members of an interface it declares.
- **Interface Segregation**: consumer-defined, narrow interfaces. Interfaces with more than
  five members MUST be justified; no "god" service abstractions.
- **Dependency Inversion**: depend on abstractions owned by the *consuming* layer. Concrete
  Infrastructure classes MUST NOT appear in Application or Domain constructor signatures.

Static analysis MUST be enabled (`<AnalysisLevel>latest-recommended</AnalysisLevel>`,
`TreatWarningsAsErrors=true`) and MUST NOT be suppressed without an inline justification
comment naming the rule and the reason.

*Rationale*: SOLID is what keeps the Dependency Rule enforceable in practice; without it, layers
stay nominally separate while coupling leaks through fat interfaces and concrete dependencies.

### III. Test-First & Unit Test Coverage (NON-NEGOTIABLE)

- Tests MUST be written before the implementation for every new behavior, and MUST be observed
  failing before the implementing code is written (Red-Green-Refactor).
- Every use case handler, domain entity with invariants, and domain service MUST have unit
  tests. A pull request adding or changing behavior without accompanying tests MUST be rejected.
- **Coverage floors, enforced in CI as a hard gate**: Domain ≥ 90% line coverage,
  Application ≥ 85%, Infrastructure ≥ 60%, React `src/` ≥ 80%. A merge that lowers any layer
  below its floor fails the build.
- Unit tests MUST NOT touch PostgreSQL, Redis, RabbitMQ, the network, the clock, or the file
  system. Time MUST be injected via `TimeProvider`/`IClock`. Tests taking longer than 100 ms
  each are integration tests and belong in the integration suite.
- Integration tests are REQUIRED for: repository implementations, cache invalidation paths,
  message publish/consume round-trips, authentication and authorization flows, and every HTTP
  and SignalR contract. They MUST run against real PostgreSQL, Redis, and RabbitMQ instances
  via Testcontainers — mocking these in integration tests is prohibited.
- Every fixed defect MUST first be reproduced by a failing regression test.
- Flaky tests MUST be fixed or deleted within one working day. Skipping, muting, or retrying a
  test to make CI green is prohibited.

*Rationale*: Chat correctness failures — lost messages, cross-tenant leakage, duplicate
delivery — are invisible until they are catastrophic. Tests are the only mechanism that catches
them before users do.

### IV. Security by Default

- Every endpoint, hub method, and consumer is **deny-by-default**. Authorization MUST be an
  explicit, positive decision made in the Application layer; an endpoint without an explicit
  authorization policy MUST fail the build.
- Authorization MUST be resource-scoped, not merely role-scoped: access to a message, channel,
  or file is checked against the caller's membership of that specific resource on every request,
  including cache hits and message-bus consumption.
- All untrusted input MUST be validated at the Presentation boundary and re-validated as domain
  invariants. Validation in React is a usability feature, never a security control.
- Secrets, connection strings, and signing keys MUST NOT appear in source, `appsettings*.json`,
  container images, logs, or test fixtures. They are supplied at runtime by the platform secret
  store.
- Logs and telemetry MUST NOT contain message bodies, attachment contents, credentials, tokens,
  or personal data beyond a stable user identifier.
- Any change to authentication, authorization, cryptography, tenant isolation, or data retention
  requires a second reviewer with security sign-off recorded in the PR.

Concrete controls are specified in **Security Requirements** below and are binding.

*Rationale*: An internal chat system concentrates an organization's most sensitive unstructured
data. Defaults decide breach outcomes; opt-in security is security that gets forgotten.

### V. Performance & Scalability Budgets

- Every feature MUST declare its latency and throughput budget in its plan before
  implementation. Budgets are acceptance criteria, not aspirations.
- Performance MUST be measured, not asserted. Claims of "fast enough" without a benchmark or
  load-test result MUST be rejected in review.
- All API instances MUST be stateless and horizontally scalable. Per-instance in-memory session
  or connection state that breaks under multi-node deployment is prohibited; shared state lives
  in PostgreSQL or Redis.
- Every list or history endpoint MUST be paginated with a bounded maximum page size. Unbounded
  result sets are prohibited.
- N+1 query patterns are a build-blocking defect. Every EF Core query executing against a table
  expected to exceed 100k rows MUST have a supporting index verified by an execution plan.
- Any operation exceeding 1 second, or any work not required for the caller's response, MUST be
  moved to a RabbitMQ consumer.

Concrete budgets are specified in **Performance Requirements** below and are binding.

*Rationale*: Chat is a latency-perceived product — users experience a 400 ms send delay as
brokenness. Budgets set before implementation are the only ones that survive it.

### VI. Asynchronous Messaging Contracts

- Cross-boundary side effects — notifications, search indexing, webhooks, analytics, retention
  jobs, fan-out to external systems — MUST be published to RabbitMQ, not executed inline within
  the request.
- Every consumer MUST be **idempotent**. Delivery is at-least-once; consumers MUST deduplicate
  on the message's idempotency key and MUST produce the same outcome when redelivered.
- Message contracts MUST be explicit, versioned types. Changes MUST be additive and
  backward-compatible; a breaking change requires a new version published alongside the old one
  until all consumers have migrated.
- Every queue MUST have a dead-letter queue, a bounded retry policy with exponential backoff,
  and an alert on DLQ depth. Silent message loss is a Sev-1 defect.
- Database writes and message publishes MUST NOT be dual-written without atomicity. Events
  affecting user-visible state MUST use the transactional outbox pattern.
- Consumers MUST NOT assume ordering across queues. Where per-channel ordering is required, it
  MUST be enforced explicitly by partition key.

*Rationale*: At-least-once delivery is the only guarantee the broker offers. Correctness must
come from idempotent consumers and outbox atomicity, not from hope that each message arrives
exactly once.

### VII. Data Authority & Cache Discipline

- **PostgreSQL is the single source of truth.** Redis is a cache and a transient coordination
  store only. Losing the entire Redis cluster MUST cause degraded latency, never data loss or
  incorrect authorization.
- Every cached entry MUST have an explicit TTL. Unbounded cache entries are prohibited.
- Cache invalidation MUST be part of the same use case that mutates the underlying data, and
  MUST be covered by an integration test.
- Authorization decisions MUST NOT be served from a cache with a TTL exceeding 60 seconds, and
  revocation of access MUST invalidate the relevant entries immediately.
- All schema changes MUST ship as reviewed, forward-only EF Core migrations that are safe to
  apply to a live database while the previous application version is still running. Destructive
  changes use the expand-migrate-contract sequence across separate releases.
- Message content, channel membership, and audit records MUST NOT be hard-deleted by application
  code outside an explicit, audited retention or data-subject-erasure workflow.

*Rationale*: The most common cause of "the chat shows the wrong thing" is a cache that was
allowed to become authoritative. Ranking the stores explicitly makes every failure mode
recoverable.

### VIII. Zero-Cost, Self-Hosted, Open Source Only

- Every runtime component, build tool, and test dependency MUST be free of charge and
  self-hostable. Paid SaaS, commercial licenses, per-seat tooling, cloud-managed services, and
  anything requiring a credit card, quota, or usage meter are prohibited.
- "Free tier" is NOT acceptable when the tier can expire, throttle, or convert to paid. Only
  software that is free in perpetuity for this organization's use qualifies.
- Every dependency MUST carry an OSI-approved license permitting internal commercial use. New
  dependencies MUST record their license in the PR description. AGPL-licensed components are
  permitted only when self-hosted and unmodified.
- Every third-party runtime component MUST be available as a Linux Docker image that runs on a
  developer laptop with no external account. If it cannot run offline in Docker Compose, it MUST
  NOT be adopted.
- No feature may depend on a hosted API for correctness. Where an external capability is
  genuinely needed, it MUST sit behind an Application-layer interface with a working
  self-hosted or no-op implementation as the default.
- Vendor-specific cloud primitives (managed queues, managed caches, proprietary identity
  services, provider-only storage APIs) are prohibited in Infrastructure code. Portable
  protocols only: SQL, AMQP, RESP, OIDC, S3-compatible HTTP.

*Rationale*: An internal tool that quietly acquires a monthly bill or a vendor lock-in becomes a
procurement problem instead of an engineering one. Constraining the stack to self-hostable open
source keeps the entire system reproducible on a laptop and deployable anywhere, forever.

## Technology Stack & Platform Constraints

The following stack is mandatory. Introducing an alternative to any component requires a
constitution amendment. Every entry is free of charge, open source, and runs as a Linux Docker
container with no external account (Principle VIII).

| Concern | Mandated technology | Binding constraints |
| --- | --- | --- |
| Backend runtime | .NET 10 / ASP.NET Core 10, C# 14 | Nullable reference types enabled solution-wide; `TreatWarningsAsErrors=true`; async/await end-to-end with no `.Result`/`.Wait()`/`.GetAwaiter().GetResult()` |
| HTTP API | ASP.NET Core Minimal APIs or controllers | OpenAPI document generated and versioned; URL-versioned (`/api/v1/...`); Problem Details (RFC 9457) for all errors |
| Real-time transport | ASP.NET Core SignalR with Redis backplane | Hubs contain no business logic; every hub method delegates to an Application use case |
| Frontend | React 19 + TypeScript (`strict: true`) | No `any` without an inline justification; server state via TanStack Query; functional components and hooks only |
| Relational store | PostgreSQL 17+ via EF Core 10 | Parameterized access only; raw SQL requires reviewer approval and parameterization; migrations forward-only |
| Cache & coordination | Redis 7+ | Cache-aside pattern; namespaced keys `internalchat:{env}:{entity}:{id}`; mandatory TTLs |
| Messaging | RabbitMQ 4+ | Durable queues, publisher confirms, manual consumer acknowledgement, DLQ per queue |
| Auth | OpenID Connect via self-hosted Keycloak (Apache-2.0) | No locally stored passwords; MFA enforced by the IdP; standard OIDC only, no vendor-specific extensions |
| Object storage | MinIO or any S3-compatible self-hosted server | S3 API only; no provider-specific SDK calls; presigned URLs for attachment access |
| Observability | OpenTelemetry SDK → self-hosted OTel Collector, Prometheus, Grafana, Loki, Jaeger | W3C trace context propagated across HTTP, SignalR, and RabbitMQ; no hosted APM |
| Containers | Docker + Docker Compose, Linux images | Images run as a non-root user; health and readiness probes required; multi-stage builds |
| CI | Self-hosted or free-tier-unlimited runners executing plain scripts | Every CI step MUST be reproducible locally via `docker compose` or a shell script; no CI-vendor-only features |
| Load testing | k6 (AGPL-3.0, self-hosted) or NBomber OSS | Runs against the local Compose stack; no hosted load-testing service |

Additional constraints:

- No Entity Framework `DbContext` may be referenced outside the Infrastructure project.
- No direct `HttpClient`, Redis, or RabbitMQ client usage outside Infrastructure.
- New third-party dependencies require review justification covering license, cost (which MUST
  be zero), maintenance status, and known CVEs.
- Redis is used under its current free license for unmodified self-hosted deployment. If a
  strictly permissive license becomes a requirement, Valkey (BSD-3-Clause) is the approved
  drop-in replacement and this row may be swapped by PATCH amendment, since both speak RESP and
  the code depends only on the protocol.
- Developer tooling MUST NOT require a paid license: the .NET SDK, VS Code with the C#
  extension, Docker Engine / Docker Desktop under its free-use terms, and the `dotnet` CLI are
  sufficient to build, run, test, and debug the entire system.

## Local Development & Deployment (Docker-First)

### Local Environment

- A single `docker compose up` from a clean checkout MUST bring the entire system up —
  API, web, PostgreSQL, Redis, RabbitMQ, Keycloak, MinIO, and the observability stack — with no
  external account, no VPN, and no manual setup step.
- Onboarding MUST be: clone, `cp .env.example .env`, `docker compose up`. Any additional
  required step is a defect to be automated away, not documented.
- Compose MUST seed a working development realm, users, database schema, and sample data so a
  developer can log in and send a message on first run.
- Compose MUST run fully offline after the first image pull. A feature that cannot be developed
  or tested without internet access violates Principle VIII.
- Every service MUST expose a health check in Compose, and dependent services MUST wait on it.
- Secrets in local Compose come from `.env`, which is git-ignored. `.env.example` holds only
  non-secret placeholders and MUST be kept current.
- Ports, volumes, and image tags MUST be pinned explicitly. `latest` tags are prohibited in any
  Compose file.

### Build and Image Requirements

- Every deployable component ships as a multi-stage Dockerfile producing a minimal runtime image
  (`mcr.microsoft.com/dotnet/aspnet` runtime base for the API; a static web server image for the
  React build output). SDK layers MUST NOT reach the final image.
- Images MUST run as a non-root user, declare `HEALTHCHECK`, and contain no secrets, `.env`
  files, test fixtures, or source code.
- Builds MUST be reproducible from the repository alone, with pinned base-image digests.
- Image builds are part of CI, and a failing image build blocks merge.

### Deployment Strategy

- **v1 target is Docker Compose on a single Linux host.** This is the deployment the system MUST
  work on, and the only one that may be assumed by operational documentation.
- **One exception, real-time media**: a second Linux host running Docker Compose MAY be dedicated
  to video-meeting and screen-sharing media relay, because media relay is CPU- and
  bandwidth-bound in a way no other component is, and colocating it would let a busy meeting
  degrade messaging for everyone. This exception covers exactly one additional host and no other
  concern. Everything except media relay stays on the application host.
- The application MUST remain fully functional with the media host absent or unreachable:
  messaging, attachments, search, and notifications MUST degrade to "meetings unavailable", never
  to an outage.
- Kubernetes, service meshes, and managed orchestration are explicitly OUT OF SCOPE until
  Compose demonstrably cannot meet a stated requirement. Adopting one requires a constitution
  amendment, not a plan-level decision.
- Application code MUST remain orchestrator-agnostic: configuration by environment variable,
  state in PostgreSQL/Redis only, no host-local file dependencies, no in-process scheduling that
  breaks with multiple replicas. Moving to multi-node orchestration later MUST require zero
  application code changes.
- Deployment MUST be a single scripted command, and rollback MUST be pinning the previous image
  tag and re-running it.
- Database migrations MUST run as an explicit, idempotent step separate from application start,
  so that a restarting container never races another on schema changes.
- Backups of the PostgreSQL volume and the MinIO volume MUST be scripted and restore-tested
  before the first production deployment.

## Security Requirements

These requirements are binding and verified in CI and in review. The system targets **OWASP
ASVS Level 2** and MUST have no unmitigated finding from the **OWASP Top 10**.

### Identity and Access

- Authentication is OpenID Connect / OAuth 2.1 against the self-hosted Keycloak IdP. Access
  tokens MUST expire within 15 minutes; refresh tokens MUST rotate on use and be revocable.
- Token signature, issuer, audience, and expiry MUST be validated on every request, including
  SignalR connection establishment and reconnection.
- Deprovisioning in the IdP MUST revoke chat access within 5 minutes.
- Administrative actions require re-authentication within the preceding 15 minutes.

### Data Protection

- TLS 1.3 (1.2 minimum) for all external traffic, including WebSocket upgrades. Plaintext HTTP
  is refused, not redirected, for API routes. Certificates are issued free of charge — Let's
  Encrypt via a self-hosted reverse proxy in production, a locally generated development CA in
  Compose. Purchased certificates are neither required nor permitted as a dependency.
- All connections to PostgreSQL, Redis, and RabbitMQ MUST use TLS with certificate validation.
- Encryption at rest MUST be enabled for the database, backups, object storage, and Redis
  persistence.
- Uploaded attachments MUST be virus-scanned by self-hosted ClamAV before becoming retrievable,
  served with
  `Content-Disposition: attachment` and a non-executable content type, and stored outside the
  web root behind short-lived signed URLs.

### Application Controls

- All database access is parameterized. String-concatenated SQL is prohibited without exception.
- React MUST NOT use `dangerouslySetInnerHTML` except with a reviewed sanitizer allow-list for
  rendered message markup.
- Content Security Policy MUST be set with no `unsafe-inline` and no `unsafe-eval`, alongside
  `Strict-Transport-Security`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, and
  `X-Frame-Options: DENY`.
- CORS MUST use an explicit origin allow-list. Wildcard origins are prohibited.
- Rate limiting MUST be applied per user and per IP on authentication, message send, search,
  and file upload endpoints.
- Errors returned to clients MUST NOT expose stack traces, SQL, or internal hostnames.

### Auditing and Compliance

- An append-only audit log MUST record authentication events, authorization denials, channel
  membership changes, permission changes, data exports, retention deletions, and administrative
  actions — with actor, subject, timestamp, source IP, and outcome. Retention: 1 year minimum.
- Audit records MUST NOT be modifiable or deletable by application code.
- Personal data handling MUST support export and erasure requests within the organization's
  stated SLA, and the data-retention policy MUST be enforced by an automated job.

### Supply Chain

- Dependency and container scanning MUST run on every pull request using free, self-hostable
  tooling: Trivy for images and filesystems, `dotnet list package --vulnerable` and `npm audit`
  for packages. A Critical or High severity vulnerability with a fix available blocks merge;
  remediation SLA is 7 days for Critical, 30 days for High.
- Static application security testing (Semgrep OSS rules and the .NET security analyzers) and
  secret scanning (Gitleaks) MUST run on every pull request and block on findings.
- All security tooling MUST be runnable locally by a developer with the same configuration CI
  uses. A gate that can only be reproduced inside a paid service is prohibited.

## Performance Requirements

Budgets are measured at the stated percentile under expected peak load, and regressions MUST
fail the performance gate in CI.

### Backend Latency (server-side, excluding client network)

| Operation | p95 | p99 |
| --- | --- | --- |
| Send message (API accept) | 150 ms | 300 ms |
| Message delivered to connected recipients (end-to-end) | 500 ms | 1 s |
| Load channel history (50 messages) | 250 ms | 500 ms |
| Channel and user list | 200 ms | 400 ms |
| Full-text message search | 800 ms | 1.5 s |
| Authentication / token exchange | 300 ms | 600 ms |

### Frontend (p75 field data, mid-tier hardware)

- Largest Contentful Paint < 2.5 s; Interaction to Next Paint < 200 ms; Cumulative Layout
  Shift < 0.1.
- Initial JavaScript bundle < 300 KB gzipped; routes beyond the primary chat view are
  code-split.
- Message lists exceeding 100 items MUST be virtualized.
- The UI MUST remain interactive and MUST show explicit reconnection state during transport
  loss; queued outbound messages MUST survive a reconnect.

### Scale and Capacity

- A single API node MUST sustain 10,000 concurrent SignalR connections at ≤ 70% CPU. This is
  verified on the local Docker Compose stack and is the binding v1 capacity requirement.
- The system MUST *be architecturally capable* of scaling horizontally to 50,000 concurrent
  users and 1,000 messages/second with no change to application code. Because the v1 deployment
  is single-host Compose, this is enforced by design review and by running the API at 3 replicas
  behind the Compose reverse proxy in integration tests — not by a production load test at that
  scale.
- Read-path cache hit ratio for channel metadata and membership MUST exceed 90% in steady state.
- Availability target: 99.5% monthly for the messaging path on the single-host Compose
  deployment. The 99.9% target applies only once a multi-node deployment is adopted; claiming
  99.9% on one host with one PostgreSQL instance would be dishonest, since a host restart alone
  exceeds that budget.
- Backup and restore of PostgreSQL MUST be exercised at least quarterly, since single-host
  deployment makes restore time the real availability ceiling.
- Real-time meeting media MUST sustain 25 participants in one session and 1,250 concurrent
  participants platform-wide on the dedicated media host, without messaging latency (SC-006)
  degrading for anyone on the application host. This separation is verified by load-testing both
  workloads simultaneously, not independently.

### Resource Discipline

- Any database query exceeding 100 ms MUST be logged with its parameters redacted and reviewed.
- Connection pooling MUST be configured with explicit bounds for PostgreSQL, Redis, and
  RabbitMQ; unbounded pools are prohibited.
- Every outbound call MUST have an explicit timeout, and every network dependency MUST be
  wrapped in a retry-with-backoff and circuit-breaker policy.
- Load tests covering the messaging path MUST run before every production release.

## Development Workflow & Quality Gates

### Branching and Review

- Work happens on short-lived branches off `main`; `main` is protected and always deployable.
- Every change requires at least one approving review. Changes touching authentication,
  authorization, cryptography, tenant isolation, retention, or database migrations require two,
  one with security sign-off.
- Commits and pull requests MUST reference the feature specification they implement.

### Required CI Gates (all blocking)

1. Build with `TreatWarningsAsErrors=true`; analyzers and format checks clean.
2. Architecture tests verifying the Dependency Rule (Domain references nothing outward;
   Application does not reference Infrastructure; Presentation does not reference Infrastructure).
3. Unit tests pass; per-layer coverage floors from Principle III met.
4. Integration tests pass against Testcontainers-provisioned PostgreSQL, Redis, and RabbitMQ.
5. Contract tests validate the OpenAPI document and message contracts for backward compatibility.
6. Security: SAST, dependency scan, container scan, secret scan.
7. Performance smoke test on the messaging path against the budgets above.
8. Frontend: TypeScript `strict` compile, ESLint, unit tests, bundle-size budget, and
   accessibility checks (WCAG 2.1 AA on interactive components).
9. Docker: every image builds, and `docker compose up` reaches a healthy state for all services
   from a clean checkout.
10. Cost: no new dependency introduces a paid service, a license fee, or a metered free tier
    (Principle VIII). Every CI gate above runs on free tooling and is reproducible locally.

### Definition of Done

A change is done when: tests were written first and pass; coverage floors hold; the declared
performance budget is measured and met; security controls for the touched surface are verified;
observability (structured logs, metrics, traces) is in place for new paths; database migrations
apply and roll forward cleanly; the OpenAPI document and any message contracts are updated; and
user-facing behavior changes are documented.

### Complexity

Simplicity is the default. Any additional project, abstraction layer, framework, or
infrastructure component MUST be justified in the plan's Complexity Tracking table with the
simpler alternative that was rejected and why. "We might need it later" is not a justification.

## Governance

- This constitution supersedes all other engineering practices, style guides, and team
  conventions. Where a document conflicts with it, this constitution wins.
- **Amendments** require: a written proposal stating the change and its rationale; approval by
  the engineering lead and one other maintainer; a migration plan for any existing code the
  change makes non-compliant; and an update to this file with an incremented version and a
  refreshed Sync Impact Report.
- **Versioning policy** (semantic):
  - MAJOR — a principle is removed or redefined in a backward-incompatible way, or governance
    itself changes.
  - MINOR — a principle or mandatory section is added, or existing guidance is materially
    expanded.
  - PATCH — clarifications, wording, and non-semantic refinements.
- **Compliance review**: every pull request is reviewed against these principles, and reviewers
  MUST cite the specific principle when requesting changes. A quarterly audit samples merged
  changes for drift, and findings become tracked remediation work.
- **Exceptions** are time-boxed. Any deviation MUST be recorded in the feature plan's Complexity
  Tracking table with an owner and a removal date. Undocumented deviations are defects.
- Runtime and agent development guidance lives in `CLAUDE.md` and the active feature plan under
  `specs/[###-feature-name]/plan.md`; those documents MUST NOT contradict this constitution.

**Version**: 1.2.0 | **Ratified**: 2026-07-31 | **Last Amended**: 2026-07-31
