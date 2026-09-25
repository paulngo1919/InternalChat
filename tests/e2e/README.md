# End-to-end tests

Playwright, driving a real browser against a **running Compose stack**. One spec per quickstart
scenario, named for it.

## Running

```bash
docker compose -f deploy/docker-compose.yml up -d
npm --prefix tests/e2e install
npm --prefix tests/e2e run install-browsers
npm --prefix tests/e2e test
```

These do not start a server of their own. The behaviour under test spans nginx, the API, Keycloak,
Redis, and PostgreSQL, and a Playwright `webServer` block that started only the SPA would produce
tests that pass with most of the system absent.

## Configuration

| Variable | Default | Purpose |
| --- | --- | --- |
| `E2E_BASE_URL` | `http://localhost:8080` | Where the web app is served |
| `E2E_KEYCLOAK_URL` | `http://localhost:8082` | Realm admin API |
| `E2E_REALM` | `internalchat` | Realm name |
| `E2E_KEYCLOAK_ADMIN` | `admin` | From `deploy/.env` |
| `E2E_KEYCLOAK_ADMIN_PASSWORD` | `change-me-keycloak` | From `deploy/.env` — override if you changed it |
| `IDLE_MS` | `12000` | `v5-idle-delivery`: how long the platform is left quiet before each probe (002 FR-002). More than twice the 5 s backstop poll; the nightly job sets `600000`, the spec's 10 minutes |

## What these tests do to the stack

`v1-signin-revocation.spec.ts` **disables a realm user** and re-enables it afterwards. That is the
scenario, not a shortcut: disabling in Keycloak ends the sessions and triggers the back-channel
logout callback that writes this platform's revocation set, which is the path a real departure
takes.

Consequences worth knowing:

- Run against a development stack only. Pointed at anything real, these tests disable a real
  account.
- `workers: 1`, because two workers would revoke each other's sessions and both would report a
  failure neither caused.
- `retries: 0`. A flaky end-to-end test is a defect report; retrying until green is how a genuine
  race in delivery or revocation gets filed as "just flaky".

## Division of labour with the integration suite

These prove the scenario end to end through a real browser. They are not where a mechanism is
pinned down — that belongs in `tests/Integration`, which can seed exact state and assert on the
socket itself.

Concretely, for revocation: `tests/Integration/Authorization/RevocationTests.cs` asserts that an
**idle** hub connection closes with no client activity at all, which is the case SC-018 is really
about and which a browser test cannot isolate. The spec here asserts that an employee's session
visibly stops working within the five minutes FR-003 allows.
