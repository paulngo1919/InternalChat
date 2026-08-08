# Implementation Plan: [FEATURE]

**Branch**: `[###-feature-name]` | **Date**: [DATE] | **Spec**: [link]

**Input**: Feature specification from `/specs/[###-feature-name]/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command. See `.specify/templates/plan-template.md` for the execution workflow.

## Summary

[Extract from feature spec: primary requirement + technical approach from research]

## Technical Context

<!--
  ACTION REQUIRED: Replace the content in this section with the technical details
  for the project. The structure here is presented in advisory capacity to guide
  the iteration process.
-->

**Language/Version**: [e.g., Python 3.11, Swift 5.9, Rust 1.75 or NEEDS CLARIFICATION]

**Primary Dependencies**: [e.g., FastAPI, UIKit, LLVM or NEEDS CLARIFICATION]

**Storage**: [if applicable, e.g., PostgreSQL, CoreData, files or N/A]

**Testing**: [e.g., pytest, XCTest, cargo test or NEEDS CLARIFICATION]

**Target Platform**: [e.g., Linux server, iOS 15+, WASM or NEEDS CLARIFICATION]

**Project Type**: [e.g., library/cli/web-service/mobile-app/compiler/desktop-app or NEEDS CLARIFICATION]

**Performance Goals**: [domain-specific, e.g., 1000 req/s, 10k lines/sec, 60 fps or NEEDS CLARIFICATION]

**Constraints**: [domain-specific, e.g., <200ms p95, <100MB memory, offline-capable or NEEDS CLARIFICATION]

**Scale/Scope**: [domain-specific, e.g., 10k users, 1M LOC, 50 screens or NEEDS CLARIFICATION]

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

*Source: `.specify/memory/constitution.md` v1.0.0. Mark each gate PASS / FAIL / N/A with a
one-line justification. Any FAIL blocks the plan until resolved or recorded in Complexity
Tracking below.*

| # | Gate | Status | Notes |
| --- | --- | --- | --- |
| I | **Clean Architecture** — layer for each new type identified; dependencies point inward; no Infrastructure types leak into Application/Presentation; no Domain entity crosses a boundary | | |
| II | **SOLID** — one responsibility per new type; consumer-owned narrow interfaces; no concrete Infrastructure in Application/Domain constructors | | |
| III | **Test-First** — unit tests written before implementation; per-layer coverage floors (Domain 90% / Application 85% / Infrastructure 60% / React 80%) held; integration tests planned for every repository, cache, messaging, auth, and contract path (Testcontainers, not mocks) | | |
| IV | **Security by Default** — explicit deny-by-default authorization policy per endpoint/hub/consumer; resource-scoped checks; boundary validation; no secrets in source; no sensitive data in logs; security reviewer required if auth/crypto/isolation/retention touched | | |
| V | **Performance Budgets** — latency/throughput budget declared below and measurable; stateless; all lists paginated; no N+1; indexes verified; >1 s work moved to RabbitMQ | | |
| VI | **Messaging Contracts** — consumers idempotent on an idempotency key; versioned backward-compatible contracts; DLQ + bounded retry; transactional outbox for user-visible state | | |
| VII | **Data Authority & Cache** — PostgreSQL is source of truth; every cache entry has a TTL; invalidation in the mutating use case and integration-tested; authz cache TTL ≤ 60 s; forward-only, live-safe migrations | | |
| VIII | **Zero-Cost & Self-Hosted** — no paid service, license fee, or metered free tier; every new component is OSI-licensed and runs as a Linux Docker image offline; no vendor-specific cloud primitives in Infrastructure | | |
| — | **Stack** — mandated stack only (ASP.NET Core 10 / React 19+TS / PostgreSQL 17 / Redis 7 / RabbitMQ 4 / Keycloak / MinIO); any new dependency justified with its license | | |
| — | **Docker-First** — new services added to `docker-compose.yml` with health checks and pinned tags; `docker compose up` still works from a clean checkout; multi-stage Dockerfile for anything deployable; no Kubernetes assumptions; single application host, with the dedicated media host as the only permitted second host and the app still functional without it | | |

**Declared performance budget for this feature**: [operation → p95 / p99 targets, and how they
will be measured]

**Declared security surface for this feature**: [authN/authZ changes, new data classes handled,
new external inputs, audit events emitted]

## Project Structure

### Documentation (this feature)

```text
specs/[###-feature]/
├── plan.md              # This file (/speckit-plan command output)
├── research.md          # Phase 0 output (/speckit-plan command)
├── data-model.md        # Phase 1 output (/speckit-plan command)
├── quickstart.md        # Phase 1 output (/speckit-plan command)
├── contracts/           # Phase 1 output (/speckit-plan command)
└── tasks.md             # Phase 2 output (/speckit-tasks command - NOT created by /speckit-plan)
```

### Source Code (repository root)
<!--
  ACTION REQUIRED: Replace the placeholder tree below with the concrete layout
  for this feature. Delete unused options and expand the chosen structure with
  real paths (e.g., apps/admin, packages/something). The delivered plan must
  not include Option labels.
-->

```text
# [REMOVE IF UNUSED] Option 1: Single project (DEFAULT)
src/
├── models/
├── services/
├── cli/
└── lib/

tests/
├── contract/
├── integration/
└── unit/

# [REMOVE IF UNUSED] Option 2: Web application (when "frontend" + "backend" detected)
backend/
├── src/
│   ├── models/
│   ├── services/
│   └── api/
└── tests/

frontend/
├── src/
│   ├── components/
│   ├── pages/
│   └── services/
└── tests/

# [REMOVE IF UNUSED] Option 3: Mobile + API (when "iOS/Android" detected)
api/
└── [same as backend above]

ios/ or android/
└── [platform-specific structure: feature modules, UI flows, platform tests]
```

**Structure Decision**: [Document the selected structure and reference the real
directories captured above]

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

| Violation | Why Needed | Simpler Alternative Rejected Because | Owner | Removal Date |
| --- | --- | --- | --- | --- |
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] | [name] | [YYYY-MM-DD] |
| [e.g., extra caching tier] | [specific problem] | [why Redis cache-aside insufficient] | [name] | [YYYY-MM-DD] |

> Per Constitution Governance, every deviation is time-boxed and MUST carry an owner and a
> removal date. Undocumented deviations are defects.
