<!-- SPECKIT START -->
For additional context about technologies to be used, project structure,
shell commands, and other important information, read the current plan:

**Active plan**: `specs/002-realtime-message-delivery/plan.md` — instant message delivery
(outbox wake-on-commit, pipelined publish, parallel fan-out, delivery-latency gates). Supporting
artifacts in the same directory: `research.md` (as-built latency analysis R0 + decisions R1–R7),
`data-model.md`, `contracts/` (hub 1.1.0 delta, messaging delta, telemetry endpoint), `quickstart.md`.

**Base plan** (stack, structure, and all product scope): `specs/001-enterprise-chat-platform/plan.md`

Supporting artifacts in the same directory:

- `spec.md` — what to build and why (9 user stories, 57 FR, 25 SC)
- `research.md` — 14 technical decisions with rationale and a dependency licence register
- `data-model.md` — entities, constraints, indexes, state transitions, Redis keyspace
- `contracts/` — `openapi.yaml`, `signalr-hub.md`, `messaging.md`
- `quickstart.md` — how to run the stack and validate each user story

**Governing document**: `.specify/memory/constitution.md` (v1.2.0). It overrides convention.
The rules most likely to bite: Clean Architecture dependency direction, test-first with enforced
per-layer coverage floors, deny-by-default resource-scoped authorization, and Principle VIII —
every dependency must be free, OSI-licensed, and self-hostable in Docker.
<!-- SPECKIT END -->
