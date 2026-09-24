# Load tests

k6 scripts, one per row of the performance budget in
[plan.md](../../specs/001-enterprise-chat-platform/plan.md).

## The rule this directory exists to enforce

> Load tests run against a database seeded to full retention volume, not an empty one. **A budget
> verified on a thousand rows is not verified.** — plan.md

This is not a nicety. Every number in the budget is dominated by whether an index is used, and
PostgreSQL chooses a plan from statistics. On a small table a sequential scan is genuinely cheaper,
so the planner picks one, the query is fast, and the test passes — while the plan it just validated
is the opposite of the one production will use. The first honest measurement then happens after
deployment.

The shapes that only appear at volume:

- **Keyset pagination** on `(conversation_id, seq)` is fast at 125 million rows and indistinguishable
  from `OFFSET` at ten thousand. A regression to `OFFSET` passes an empty-database test.
- **Monthly partitioning** only prunes when there are partitions to prune. On one month of data,
  partition elimination is untested.
- **The membership index** is partial (`WHERE removed_at IS NULL`); its value is proportional to how
  much removed-membership churn it excludes, which is zero on a fresh database.

## Running

```bash
# 1. Bring up the stack.
docker compose -f deploy/docker-compose.yml up -d

# 2. Seed to full retention volume. ~125 million messages spread over 12 months, at roughly
#    80,000 rows/s measured — about 26 minutes. Do this once and snapshot the volume.
dotnet run --project tools/Seeder -- --dev
dotnet run --project tools/Seeder -- --load 125000000

# 3. Measure.
k6 run -e BASE_URL=http://localhost:8081 -e KEYCLOAK_URL=http://localhost:8082 \
       tests/Load/messaging.js
```

`--dev` is required before `--load`: the load seeder bulk-loads messages into conversations, and the
employees, conversations, and memberships come from the development seed.

### Keeping the seeded volume

26 minutes is long enough that re-seeding per run will not happen, and a run against a partially
seeded database is worse than no run because it reports a number somebody will quote. Snapshot the
volume instead:

```bash
docker compose -f deploy/docker-compose.yml stop postgres
docker run --rm -v internalchat_postgres-data:/from -v "$PWD/.loadvolume":/to alpine \
  sh -c 'cd /from && tar cf /to/postgres-loaded.tar .'
```

Restore by reversing it. `.loadvolume/` is git-ignored — it is tens of gigabytes.

## What each script asserts

| Script | Budget rows | Notes |
| --- | --- | --- |
| `messaging.js` | send accept p95 150 ms / p99 300 ms; history page p95 250 ms / p99 500 ms | Holds 100 sends/s for two minutes, then bursts to 1,000/s (plan.md Scale/Scope). Reads history concurrently, because in production they compete for the same connection pool. |

Thresholds are k6 `thresholds`, so a breach exits non-zero. That is what makes this a gate rather
than a report.

### What these scripts do not measure

- **End-to-end delivery** (SC-006, p95 500 ms). That is browser-to-browser and is measured by
  `tests/e2e/v2-delivery-timing.spec.ts`. A fast accept with slow fan-out passes k6 and fails there,
  which is exactly why both exist.
- **Idempotency.** Every send here uses a fresh `clientMessageKey`, deliberately: reusing one would
  measure the deduplication read instead of the accept path. Retry correctness is asserted in
  `tests/Integration/Messages/IdempotentSendTests.cs`, where the row count can actually be checked.
- **Search** (p95 800 ms). `search.js` arrives with T169, which is also the D9 decision trigger for
  OpenSearch.

## Interpreting a breach

Take the plan before changing anything:

```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT * FROM message
WHERE conversation_id = '...' AND seq < 48213
ORDER BY seq DESC
LIMIT 50;
```

A sequential scan, a missing partition pruning line, or a sort that spills to disk each point
somewhere specific. Raising the budget is not a fix, and neither is adding an index before reading
the plan that says one is missing.

## Status

⚠️ **`messaging.js` is written but has not been run against seeded volume.** Doing so needs the
Compose images built and roughly half an hour of seeding; T218 is the task that runs the full
messaging-path load test, and T108 is satisfied by this document stating the requirement and the
method rather than by a measurement. **No number in plan.md's budget table has been verified yet.**
