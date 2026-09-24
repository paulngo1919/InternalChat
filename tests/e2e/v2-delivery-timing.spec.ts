import { expect, test, type Page } from '@playwright/test';

/**
 * T106 — quickstart V2, and the measurement behind SC-006.
 *
 * The budget is "end-to-end delivery p95 500 ms, p99 1 s" (plan.md), and plan.md names this harness
 * as how it is measured. Two real browsers, one conversation, and the wall-clock gap between the
 * sender pressing Enter and the message appearing for the recipient — which is what an employee
 * actually experiences, and is not the same as any server-side timer.
 *
 * Deliberately not a load test. k6 (`tests/Load/messaging.js`) measures the accept budget under
 * concurrency; this measures the delivery path once, through the whole stack including the browser's
 * render. Both are needed: a fast accept with slow fan-out passes k6 and fails here.
 */

/** From the development roster (deploy/keycloak/README.md). Passwords equal usernames. */
const SENDER = 'an.nguyen';
const RECIPIENT = 'binh.tran';

/**
 * Samples per run.
 *
 * Enough for a p95 to mean something, few enough to keep the run inside a sensible CI budget. A
 * single sample would measure whichever request happened to be unlucky.
 */
const SAMPLES = 20;

/** SC-006. Asserted on p95, because that is what the budget is stated as. */
const P95_BUDGET_MS = 500;
const P99_BUDGET_MS = 1_000;

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/');
  await page.getByLabel(/username|email/i).fill(username);
  await page.getByLabel(/password/i).fill(username);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await expect(page.getByTestId('display-name')).toBeVisible();
}

/** The percentile of a sample set, using nearest-rank. */
function percentile(samples: number[], p: number): number {
  const sorted = [...samples].sort((a, b) => a - b);
  const rank = Math.ceil((p / 100) * sorted.length) - 1;
  return sorted[Math.min(Math.max(rank, 0), sorted.length - 1)] ?? 0;
}

test.describe('V2 — exactly-once messaging and delivery timing', () => {
  test('a message reaches the other browser inside the delivery budget', async ({ browser }) => {
    // Two contexts, not two pages in one: they must have independent sessions, and a shared context
    // would share the token and the SignalR connection — making this measure a loopback rather than
    // delivery to another employee.
    const senderContext = await browser.newContext();
    const recipientContext = await browser.newContext();

    const sender = await senderContext.newPage();
    const recipient = await recipientContext.newPage();

    try {
      await signIn(sender, SENDER);
      await signIn(recipient, RECIPIENT);

      // Both open the same conversation. The seeded dataset includes a direct conversation between
      // roster positions 0 and 1, which is this pair (tools/Seeder DirectPairs).
      await sender.getByTestId('conversation-item').first().click();
      await recipient.getByTestId('conversation-item').first().click();

      await expect(sender.getByTestId('composer')).toBeVisible();
      await expect(recipient.getByTestId('composer')).toBeVisible();

      const durations: number[] = [];

      for (let i = 0; i < SAMPLES; i++) {
        // Unique per sample, so the wait cannot match a previous message.
        const text = `timing probe ${String(i)} ${String(Date.now())}`;

        const appeared = recipient.getByText(text);

        const started = Date.now();
        await sender.getByTestId('composer').fill(text);
        await sender.getByTestId('composer').press('Enter');

        await appeared.waitFor({ state: 'visible', timeout: 10_000 });
        durations.push(Date.now() - started);
      }

      const p95 = percentile(durations, 95);
      const p99 = percentile(durations, 99);

      // Reported unconditionally, so a passing run still records the number. A budget nobody sees
      // the measurement for is a budget that drifts.
      // eslint-disable-next-line no-console -- the measurement is the point of this test.
      console.log(
        `delivery: p50 ${String(percentile(durations, 50))} ms, p95 ${String(p95)} ms, ` +
          `p99 ${String(p99)} ms over ${String(SAMPLES)} samples`,
      );

      expect(
        p95,
        `SC-006 allows p95 ${String(P95_BUDGET_MS)} ms for end-to-end delivery; measured ${String(p95)} ms.`,
      ).toBeLessThanOrEqual(P95_BUDGET_MS);

      expect(
        p99,
        `SC-006 allows p99 ${String(P99_BUDGET_MS)} ms; measured ${String(p99)} ms.`,
      ).toBeLessThanOrEqual(P99_BUDGET_MS);
    } finally {
      await senderContext.close();
      await recipientContext.close();
    }
  });

  test('three messages sent while offline all arrive once, in order', async ({ browser }) => {
    // quickstart V2 step 3. The offline queue's unit tests (T105) pin the mechanism; this proves it
    // holds through a real browser, a real network interruption, and the real server.
    const context = await browser.newContext();
    const page = await context.newPage();

    try {
      await signIn(page, SENDER);
      await page.getByTestId('conversation-item').first().click();
      await expect(page.getByTestId('composer')).toBeVisible();

      const stamp = String(Date.now());
      const bodies = [`offline one ${stamp}`, `offline two ${stamp}`, `offline three ${stamp}`];

      await context.setOffline(true);

      for (const body of bodies) {
        await page.getByTestId('composer').fill(body);
        await page.getByTestId('composer').press('Enter');
      }

      // All three are held, visibly, rather than silently dropped — FR-018 and what the composer
      // tells the employee while offline.
      await expect(page.getByTestId('pending-message')).toHaveCount(3);

      await context.setOffline(false);

      // Each appears exactly once. A count of two for any of them would mean the retry produced a
      // second message, which is the failure SC-022 forbids and the one a stable
      // `clientMessageKey` exists to prevent.
      for (const body of bodies) {
        await expect(page.getByText(body)).toHaveCount(1, { timeout: 30_000 });
      }

      await expect(page.getByTestId('pending-message')).toHaveCount(0, { timeout: 30_000 });

      // And in composition order. The queue stops at the first failure precisely so a later message
      // cannot take a lower sequence than an earlier one — an overtake would be permanent, because
      // the server's sequence IS the order.
      const rendered = await page.getByTestId('message').allInnerTexts();
      const positions = bodies.map((body) => rendered.findIndex((text) => text.includes(body)));

      expect(positions.every((position) => position >= 0)).toBe(true);
      expect(positions).toEqual([...positions].sort((a, b) => a - b));
    } finally {
      await context.close();
    }
  });
});
