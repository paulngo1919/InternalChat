# Contracts: Enterprise Internal Chat Platform

**Date**: 2026-07-31 | **Plan**: [../plan.md](../plan.md)

Three contract surfaces. Each is versioned independently and each has a backward-compatibility test
in `tests/Contract/` that fails the build on a breaking change (Constitution CI gate 5).

| Contract | File | Surface |
| --- | --- | --- |
| HTTP API | [openapi.yaml](./openapi.yaml) | Request/response over `/api/v1` |
| Real-time hub | [signalr-hub.md](./signalr-hub.md) | SignalR client and server methods |
| Events | [messaging.md](./messaging.md) | RabbitMQ exchanges, routing keys, payloads |

## Versioning policy

Per Constitution Principle VI, contract changes MUST be additive and backward-compatible.

**Allowed without a version bump**: adding an optional request field, adding a response field,
adding a new endpoint, adding a new event type, adding an optional event field, relaxing a
validation rule.

**Requires a new version published alongside the old one**: removing or renaming a field, making an
optional field required, narrowing a type or enum, changing a status code for an existing
condition, changing routing key semantics.

HTTP versions live in the URL (`/api/v1`, `/api/v2`). Event versions live in the type name
(`chat.message.sent.v1` → `.v2`), and both versions are published until every consumer has migrated.

## Cross-cutting rules

- **Errors** use Problem Details (RFC 9457). No stack traces, SQL, or internal hostnames reach a
  client (Constitution Security Requirements).
- **Authorization** is deny-by-default. Every endpoint and hub method declares an explicit policy;
  an architecture test fails the build when one does not.
- **Pagination** is keyset-based on `(conversation_id, seq)`. Maximum page size is 100; a larger
  request is clamped, not rejected, and the response says so.
- **Idempotency**: any state-changing operation that a client may retry accepts a client-supplied
  key. For messages this is `clientMessageKey`; the server returns the existing resource with `200`
  rather than creating a duplicate.
- **Rate limits** apply per employee and per IP on authentication, message send, search, and upload.
  Exceeding one returns `429` with `Retry-After`.
