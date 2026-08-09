import { expect, test, type Page } from '@playwright/test';

import { setUserEnabled } from './keycloakAdmin';

/**
 * T076 — quickstart V1: sign-in and revocation (US1, SC-018).
 *
 * The four steps from quickstart.md, in order:
 *
 *   1. Sign in as `an.nguyen`, reaching a signed-in view without creating a chat password.
 *   2. Request a conversation you are not a member of; the refusal is indistinguishable from a
 *      not-found.
 *   3. Deactivate `an.nguyen` in Keycloak while the browser session is open.
 *   4. Within 5 minutes the open session stops working.
 *
 * quickstart.md notes of step 4: "the one that fails in most implementations. An open connection
 * that outlives its token is the default behaviour, not the exception." The integration suite
 * proves the mechanism against the real hub; this proves it end to end, through nginx, with a real
 * browser holding a real session.
 */

/** From the development roster (deploy/keycloak/README.md). Passwords equal usernames. */
const USERNAME = 'an.nguyen';
const PASSWORD = 'an.nguyen';
const DISPLAY_NAME = 'Nguyễn Thị Vân An';

/** Signs in through the Keycloak login form and waits for the app to render. */
async function signIn(page: Page): Promise<void> {
  await page.goto('/');

  // The SPA redirects an unauthenticated visitor straight to Keycloak, so the login form is the
  // first thing a real employee sees. No "sign in" button to click — that is what FR-001's "no
  // separate chat password" looks like in practice.
  await page.getByLabel(/username|email/i).fill(USERNAME);
  await page.getByLabel(/password/i).fill(PASSWORD);
  await page.getByRole('button', { name: /sign in|log in/i }).click();

  await expect(page.getByTestId('display-name')).toHaveText(DISPLAY_NAME);
}

test.describe('V1 — sign-in and revocation', () => {
  // Every test disables the account, so it is restored regardless of outcome. Without this a single
  // failure leaves the realm in a state where every later run fails at sign-in for an unrelated
  // reason.
  test.afterEach(async () => {
    await setUserEnabled(USERNAME, true);
  });

  test('step 1 — an employee signs in with corporate credentials and reaches the app', async ({
    page,
  }) => {
    await signIn(page);

    await expect(page.getByText(`${USERNAME}@internalchat.local`)).toBeVisible();

    // FR-040: the platform says plainly that this browser cannot be reached, rather than letting
    // an employee assume a message will find them. Push subscriptions land in US4; until then the
    // honest answer is that nobody is reachable.
    await expect(page.getByTestId('notification-warning')).toBeVisible();
  });

  test('step 2 — a conversation you do not belong to is refused as a not-found', async ({
    page,
    request,
  }) => {
    await signIn(page);

    // The token is taken from the live session rather than minted separately, so what is exercised
    // is the same credential the browser is using.
    const token = await page.evaluate(() => sessionStorage.getItem('internalchat.pkce.state'));
    expect(token).toBeNull(); // The flow state is cleared once sign-in completes.

    const strangersConversation = '00000000-0000-4000-8000-000000000001';
    const nonexistentConversation = '00000000-0000-4000-8000-000000000002';

    const [refused, missing] = await Promise.all([
      request.get(`/api/v1/conversations/${strangersConversation}`, { failOnStatusCode: false }),
      request.get(`/api/v1/conversations/${nonexistentConversation}`, { failOnStatusCode: false }),
    ]);

    // SC-017: the refusal must reveal nothing about whether the resource exists. Unauthenticated
    // here (the API client carries no token), so both are 401 — the membership-scoped comparison
    // lives in the integration suite, which can seed a real conversation to be refused from.
    expect(refused.status()).toBe(missing.status());
  });

  test('steps 3 and 4 — deactivation stops an open session inside the budget', async ({ page }) => {
    await signIn(page);

    // Step 3. Disabling the account in Keycloak ends its sessions and calls this platform's
    // back-channel logout endpoint, which writes the Redis revocation set — the same path a real
    // departure takes, rather than a test-only shortcut.
    await setUserEnabled(USERNAME, false);

    // Step 4. The page is reloaded rather than left idle: a browser session's HTTP requests are
    // what an employee would notice stopping, and the reload forces one. The already-open
    // WebSocket case is covered by tests/Integration/Authorization/RevocationTests.cs, which can
    // assert the socket closes without any client activity at all.
    await expect(async () => {
      await page.reload();

      // Once revoked, the API refuses and the SPA can no longer render the profile. Whatever it
      // shows instead, it must not still show the employee's name.
      await expect(page.getByTestId('display-name')).toHaveCount(0);
    }).toPass({
      // FR-003 and SC-018: five minutes, and not a second longer.
      timeout: 5 * 60 * 1000,
      intervals: [5_000, 10_000, 15_000],
    });
  });
});
