---

description: "Task list template for feature implementation"
---

# Tasks: [FEATURE NAME]

**Input**: Design documents from `/specs/[###-feature-name]/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Tests are MANDATORY per Constitution Principle III (Test-First & Unit Test Coverage,
NON-NEGOTIABLE). Every user story MUST include test tasks, they MUST be ordered before the
implementation tasks they cover, and they MUST be observed failing first. Integration tests for
repository, cache, messaging, auth, and contract paths run against real PostgreSQL, Redis, and
RabbitMQ via Testcontainers — mocking those in integration tests is prohibited.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Backend (Clean Architecture, Constitution Principle I)**: `src/InternalChat.Domain/`,
  `src/InternalChat.Application/`, `src/InternalChat.Infrastructure/`, `src/InternalChat.Api/`
- **Frontend**: `src/internalchat-web/src/`
- **Tests**: `tests/Unit/`, `tests/Integration/`, `tests/Contract/`, `tests/Architecture/`
- Adjust to the concrete layout captured in plan.md's Structure Decision

<!--
  ============================================================================
  IMPORTANT: The tasks below are SAMPLE TASKS for illustration purposes only.

  The /speckit-tasks command MUST replace these with actual tasks based on:
  - User stories from spec.md (with their priorities P1, P2, P3...)
  - Feature requirements from plan.md
  - Entities from data-model.md
  - Endpoints from contracts/

  Tasks MUST be organized by user story so each story can be:
  - Implemented independently
  - Tested independently
  - Delivered as an MVP increment

  DO NOT keep these sample tasks in the generated tasks.md file.
  ============================================================================
-->

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [ ] T001 Create project structure per implementation plan
- [ ] T002 Initialize .NET 10 solution / React 19 app with dependencies (all free and OSI-licensed)
- [ ] T003 [P] Configure linting, formatting, and analyzers (`TreatWarningsAsErrors=true`)
- [ ] T003a Add or extend `docker-compose.yml` with any new service (pinned tag, health check, `.env.example` entries) and verify `docker compose up` reaches healthy from a clean checkout

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

Examples of foundational tasks (adjust based on your project):

- [ ] T004 Setup EF Core/PostgreSQL schema and forward-only migrations framework
- [ ] T005 [P] Implement OIDC authentication and deny-by-default authorization framework
- [ ] T006 [P] Setup API routing, Problem Details middleware, and SignalR hub with Redis backplane
- [ ] T007 Create base domain entities and value objects that all stories depend on
- [ ] T008 Configure OpenTelemetry logging/metrics/tracing and the audit-log sink
- [ ] T009 Setup environment configuration and secret-store integration (no secrets in source)
- [ ] T010 [P] Setup architecture tests enforcing the Dependency Rule (Constitution Principle I)
- [ ] T011 [P] Setup Testcontainers fixtures for PostgreSQL, Redis, and RabbitMQ
- [ ] T012 Setup RabbitMQ topology: durable queues, publisher confirms, DLQ + bounded retry per queue

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - [Title] (Priority: P1) 🎯 MVP

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 1 (REQUIRED) ⚠️

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T013 [P] [US1] Unit tests for [domain rule / use case] in tests/Unit/[Layer]/[Name]Tests.cs
- [ ] T014 [P] [US1] Contract test for [endpoint] in tests/Contract/[Name]ContractTests.cs
- [ ] T015 [P] [US1] Integration test for [user journey] in tests/Integration/[Name]Tests.cs

### Implementation for User Story 1

- [ ] T016 [P] [US1] Create [Entity1] + invariants in src/InternalChat.Domain/[Area]/[Entity1].cs
- [ ] T017 [P] [US1] Create [Entity2] + invariants in src/InternalChat.Domain/[Area]/[Entity2].cs
- [ ] T018 [US1] Implement [UseCase] handler in src/InternalChat.Application/[Area]/[UseCase].cs (depends on T016, T017)
- [ ] T019 [US1] Implement [repository/cache/publisher] in src/InternalChat.Infrastructure/[Area]/[File].cs
- [ ] T020 [US1] Expose [endpoint/hub method] in src/InternalChat.Api/[Area]/[File].cs with an explicit authorization policy
- [ ] T021 [US1] Add boundary validation, Problem Details error mapping, structured logging, and audit events

**Checkpoint**: At this point, User Story 1 should be fully functional and testable independently

---

## Phase 4: User Story 2 - [Title] (Priority: P2)

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 2 (REQUIRED) ⚠️

- [ ] T022 [P] [US2] Unit tests for [domain rule / use case] in tests/Unit/[Layer]/[Name]Tests.cs
- [ ] T023 [P] [US2] Contract test for [endpoint] in tests/Contract/[Name]ContractTests.cs
- [ ] T024 [P] [US2] Integration test for [user journey] in tests/Integration/[Name]Tests.cs

### Implementation for User Story 2

- [ ] T025 [P] [US2] Create [Entity] + invariants in src/InternalChat.Domain/[Area]/[Entity].cs
- [ ] T026 [US2] Implement [UseCase] handler in src/InternalChat.Application/[Area]/[UseCase].cs
- [ ] T027 [US2] Expose [endpoint/hub method] in src/InternalChat.Api/[Area]/[File].cs with an explicit authorization policy
- [ ] T028 [US2] Integrate with User Story 1 components (if needed)

**Checkpoint**: At this point, User Stories 1 AND 2 should both work independently

---

## Phase 5: User Story 3 - [Title] (Priority: P3)

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 3 (REQUIRED) ⚠️

- [ ] T029 [P] [US3] Unit tests for [domain rule / use case] in tests/Unit/[Layer]/[Name]Tests.cs
- [ ] T030 [P] [US3] Contract test for [endpoint] in tests/Contract/[Name]ContractTests.cs
- [ ] T031 [P] [US3] Integration test for [user journey] in tests/Integration/[Name]Tests.cs

### Implementation for User Story 3

- [ ] T032 [P] [US3] Create [Entity] + invariants in src/InternalChat.Domain/[Area]/[Entity].cs
- [ ] T033 [US3] Implement [UseCase] handler in src/InternalChat.Application/[Area]/[UseCase].cs
- [ ] T034 [US3] Expose [endpoint/hub method] in src/InternalChat.Api/[Area]/[File].cs with an explicit authorization policy

**Checkpoint**: All user stories should now be independently functional

---

[Add more user story phases as needed, following the same pattern]

---

## Phase N: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] TXXX [P] Documentation updates in docs/ and the OpenAPI document
- [ ] TXXX Code cleanup and refactoring
- [ ] TXXX Verify declared performance budgets by load test (Constitution Principle V)
- [ ] TXXX [P] Raise coverage to the per-layer floors (Domain 90% / Application 85% / Infrastructure 60% / React 80%)
- [ ] TXXX Security verification: authorization policies, rate limits, headers/CSP, audit events, dependency and secret scans
- [ ] TXXX Run quickstart.md validation

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories (Phase 3+)**: All depend on Foundational phase completion
  - User stories can then proceed in parallel (if staffed)
  - Or sequentially in priority order (P1 → P2 → P3)
- **Polish (Final Phase)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) - No dependencies on other stories
- **User Story 2 (P2)**: Can start after Foundational (Phase 2) - May integrate with US1 but should be independently testable
- **User Story 3 (P3)**: Can start after Foundational (Phase 2) - May integrate with US1/US2 but should be independently testable

### Within Each User Story

- Tests MUST be written and observed FAILING before implementation (Constitution Principle III)
- Domain before Application; Application before Infrastructure; Infrastructure before Presentation
- Core implementation before integration
- Story complete before moving to next priority

### Parallel Opportunities

- All Setup tasks marked [P] can run in parallel
- All Foundational tasks marked [P] can run in parallel (within Phase 2)
- Once Foundational phase completes, all user stories can start in parallel (if team capacity allows)
- All tests for a user story marked [P] can run in parallel
- Models within a story marked [P] can run in parallel
- Different user stories can be worked on in parallel by different team members

---

## Parallel Example: User Story 1

```bash
# Launch all tests for User Story 1 together (write them FIRST, confirm they fail):
Task: "Unit tests for [domain rule / use case] in tests/Unit/[Layer]/[Name]Tests.cs"
Task: "Contract test for [endpoint] in tests/Contract/[Name]ContractTests.cs"
Task: "Integration test for [user journey] in tests/Integration/[Name]Tests.cs"

# Launch all domain entities for User Story 1 together:
Task: "Create [Entity1] + invariants in src/InternalChat.Domain/[Area]/[Entity1].cs"
Task: "Create [Entity2] + invariants in src/InternalChat.Domain/[Area]/[Entity2].cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL - blocks all stories)
3. Complete Phase 3: User Story 1
4. **STOP and VALIDATE**: Test User Story 1 independently
5. Deploy/demo if ready

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add User Story 1 → Test independently → Deploy/Demo (MVP!)
3. Add User Story 2 → Test independently → Deploy/Demo
4. Add User Story 3 → Test independently → Deploy/Demo
5. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: User Story 1
   - Developer B: User Story 2
   - Developer C: User Story 3
3. Stories complete and integrate independently

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Verify tests fail before implementing (NON-NEGOTIABLE per Constitution Principle III)
- Every new endpoint, hub method, and consumer needs an explicit authorization policy task
- Every cache write task needs a matching invalidation task and integration test
- Every RabbitMQ consumer task must state its idempotency key
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Avoid: vague tasks, same file conflicts, cross-story dependencies that break independence
