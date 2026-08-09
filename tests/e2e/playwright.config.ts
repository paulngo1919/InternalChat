import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end configuration.
 *
 * These run against a **running Compose stack**, not against a dev server Playwright starts. That
 * is deliberate: the behaviour under test spans nginx, the API, Keycloak, Redis, and PostgreSQL,
 * and a `webServer` block starting only the SPA would produce tests that pass without most of the
 * system being present.
 *
 *   docker compose -f deploy/docker-compose.yml up -d
 *   npm --prefix tests/e2e test
 */
export default defineConfig({
  testDir: '.',
  testMatch: '**/*.spec.ts',

  // No retries. A flaky end-to-end test is a defect report, and retrying until green is how a real
  // race in delivery or revocation gets filed as "just flaky".
  retries: 0,

  // Serial. The revocation scenarios deactivate shared realm users, so two workers would revoke
  // each other's sessions and both would report a failure neither caused.
  workers: 1,

  // Generous: the revocation budget in FR-003 is five minutes, and one test genuinely waits for a
  // session to stop working.
  timeout: 6 * 60 * 1000,
  expect: { timeout: 15_000 },

  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],

  use: {
    baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:8080',
    trace: 'retain-on-failure',
    video: 'retain-on-failure',
    screenshot: 'only-on-failure',

    // The dev stack terminates TLS with a self-signed certificate (deploy/scripts/
    // generate-dev-certs.sh). Accepted here and nowhere else.
    ignoreHTTPSErrors: true,
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
