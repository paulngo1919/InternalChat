import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';

/**
 * T106 — quickstart V2 — and, since 002, the regression test for the reported delivery delay.
 *
 * The budget is 002 SC-001: send pressed → visible to the online recipient, p95 300 ms / p99 500 ms.
 * It supersedes 001 SC-006 (500 ms / 1 s). Two real browsers, one conversation, and the wall-clock
 * gap between the sender pressing Enter and the message appearing for the recipient — which is what
 * an employee actually experiences, and is not the same as any server-side timer.
 *
 * Before 002 this failed at a p95 near one second: the outbox dispatcher slept a fixed second
 * whenever it found nothing to do, and each sample landed somewhere in that sleep (002 research R0).
 *
 * Deliberately not a load test. k6 (`tests/Load/messaging.js`) measures the accept budget and
 * delivery lag under concurrency; this measures the delivery path through the whole stack including
 * the browser's render. Both are needed: a fast accept with slow fan-out passes k6's accept
 * thresholds and fails here.
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
const SAMPLES = 50;

/** 002 SC-001. Asserted on p95 and p99, because that is how the budget is stated. */
const P95_BUDGET_MS = 300;
const P99_BUDGET_MS = 500;

/** 002 SC-002 — the sender's own view. */
const OWN_BUBBLE_P99_MS = 100;
const SENT_STATE_P95_MS = 300;

/** 002 SC-004 — a client back from up to five minutes offline is caught up within this. */
const CATCH_UP_BUDGET_MS = 3_000;

/**
 * A random pause before each sample, so samples cannot phase-lock with anything periodic on the
 * server. With a fixed cadence, a poll loop whose period divides it would look either always fast
 * or always slow — which is how a one-second sleep can hide from a timing test.
 */
async function jitter(page: Page): Promise<void> {
  await page.waitForTimeout(Math.floor(Math.random() * 1_500));
}

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

/**
 * Sends `text` from `sender` and returns the time until it is visible in every one of `watchers`.
 *
 * Timed from just before the keystroke, which is the moment an employee considers the message sent.
 */
async function timeDelivery(sender: Page, watchers: readonly Page[], text: string): Promise<number> {
  const started = Date.now();
  await sender.getByTestId('composer').fill(text);
  await sender.getByTestId('composer').press('Enter');

  await Promise.all(
    watchers.map((watcher) => watcher.getByText(text).waitFor({ state: 'visible', timeout: 10_000 })),
  );

  return Date.now() - started;
}

/** Opens a signed-in page in its own context, so each has an independent session and connection. */
async function openAs(browser: Browser, username: string): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await signIn(page, username);
  return { context, page };
}

/** Opens the seeded direct conversation between SENDER and RECIPIENT (tools/Seeder DirectPairs). */
async function openDirect(page: Page): Promise<void> {
  await page.getByTestId('conversation-item').first().click();
  await expect(page.getByTestId('composer')).toBeVisible();
}

function report(label: string, samples: number[]): void {
  // eslint-disable-next-line no-console -- the measurement is the point of these tests.
  console.log(
    `${label}: p50 ${String(percentile(samples, 50))} ms, p95 ${String(percentile(samples, 95))} ms, ` +
      `p99 ${String(percentile(samples, 99))} ms over ${String(samples.length)} samples`,
  );
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
        await jitter(sender);

        // Unique per sample, so the wait cannot match a previous message.
        const text = `timing probe ${String(i)} ${String(Date.now())}`;

        durations.push(await timeDelivery(sender, [recipient], text));
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
        `002 SC-001 allows p95 ${String(P95_BUDGET_MS)} ms for end-to-end delivery; measured ${String(p95)} ms.`,
      ).toBeLessThanOrEqual(P95_BUDGET_MS);

      expect(
        p99,
        `002 SC-001 allows p99 ${String(P99_BUDGET_MS)} ms; measured ${String(p99)} ms.`,
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

  test('a conversation that is not open updates its unread badge as the message arrives (002 FR-006)', async ({
    browser,
  }) => {
    const sender = await openAs(browser, SENDER);
    const recipient = await openAs(browser, RECIPIENT);

    try {
      await openDirect(sender.page);

      // The recipient is looking at nothing in particular — the direct conversation is closed.
      const row = recipient.page.getByTestId('conversation-item').first();
      await expect(row).toBeVisible();

      const text = `unread probe ${String(Date.now())}`;
      const started = Date.now();
      await sender.page.getByTestId('composer').fill(text);
      await sender.page.getByTestId('composer').press('Enter');

      await row.getByTestId('unread-count').waitFor({ state: 'visible', timeout: 10_000 });
      await expect(row.getByTestId('conversation-preview')).toHaveText(text);
      const elapsed = Date.now() - started;

      report('unread badge', [elapsed]);
      expect(elapsed, 'The unread badge must move with the message, not with a list refetch.').toBeLessThanOrEqual(
        P99_BUDGET_MS,
      );
    } finally {
      await sender.context.close();
      await recipient.context.close();
    }
  });

  test('a recipient signed in on two devices sees each message on both (US1 scenario 4)', async ({ browser }) => {
    const sender = await openAs(browser, SENDER);
    const laptop = await openAs(browser, RECIPIENT);
    const phone = await openAs(browser, RECIPIENT);

    try {
      await Promise.all([openDirect(sender.page), openDirect(laptop.page), openDirect(phone.page)]);

      const durations: number[] = [];

      for (let i = 0; i < 10; i++) {
        await jitter(sender.page);
        durations.push(await timeDelivery(sender.page, [laptop.page, phone.page], `two devices ${String(i)} ${String(Date.now())}`));
      }

      report('two devices', durations);
      expect(percentile(durations, 99)).toBeLessThanOrEqual(P99_BUDGET_MS);
    } finally {
      await sender.context.close();
      await laptop.context.close();
      await phone.context.close();
    }
  });

  test('the sender sees their own message at once and then as sent (002 SC-002)', async ({ browser }) => {
    const sender = await openAs(browser, SENDER);

    try {
      await openDirect(sender.page);

      const bubble: number[] = [];
      const sent: number[] = [];

      for (let i = 0; i < 20; i++) {
        await jitter(sender.page);
        const text = `own echo ${String(i)} ${String(Date.now())}`;

        const started = Date.now();
        await sender.page.getByTestId('composer').fill(text);
        await sender.page.getByTestId('composer').press('Enter');

        // Visible at all — optimistic or confirmed — is the moment the employee knows it worked.
        await sender.page.getByText(text).first().waitFor({ state: 'visible', timeout: 10_000 });
        bubble.push(Date.now() - started);

        // Confirmed: rendered as a stored message rather than a pending one.
        await sender.page
          .getByTestId('message')
          .filter({ hasText: text })
          .waitFor({ state: 'visible', timeout: 10_000 });
        sent.push(Date.now() - started);

        // Exactly one bubble — the optimistic one was replaced, not joined.
        await expect(sender.page.getByText(text)).toHaveCount(1);
      }

      report('own bubble', bubble);
      report('sent state', sent);

      expect(percentile(bubble, 99)).toBeLessThanOrEqual(OWN_BUBBLE_P99_MS);
      expect(percentile(sent, 95)).toBeLessThanOrEqual(SENT_STATE_P95_MS);
    } finally {
      await sender.context.close();
    }
  });

  test("the sender's other device shows what they send within the delivery budget (002 FR-005)", async ({
    browser,
  }) => {
    const desk = await openAs(browser, SENDER);
    const phone = await openAs(browser, SENDER);

    try {
      await Promise.all([openDirect(desk.page), openDirect(phone.page)]);

      const durations: number[] = [];

      for (let i = 0; i < 10; i++) {
        await jitter(desk.page);
        durations.push(await timeDelivery(desk.page, [phone.page], `other device ${String(i)} ${String(Date.now())}`));
      }

      report('sender other device', durations);
      expect(percentile(durations, 99)).toBeLessThanOrEqual(P99_BUDGET_MS);
    } finally {
      await desk.context.close();
      await phone.context.close();
    }
  });

  test('a client back from two minutes offline catches up in order, then receives live (002 SC-004)', async ({
    browser,
  }) => {
    test.setTimeout(5 * 60 * 1000);

    const sender = await openAs(browser, SENDER);
    const recipient = await openAs(browser, RECIPIENT);

    try {
      await Promise.all([openDirect(sender.page), openDirect(recipient.page)]);

      await recipient.context.setOffline(true);

      const stamp = String(Date.now());
      const missed = Array.from({ length: 20 }, (_, i) => `missed ${String(i)} ${stamp}`);

      for (const text of missed) {
        await sender.page.getByTestId('composer').fill(text);
        await sender.page.getByTestId('composer').press('Enter');
      }

      await recipient.page.waitForTimeout(120_000);

      const restored = Date.now();
      await recipient.context.setOffline(false);

      for (const text of missed) {
        await expect(recipient.page.getByText(text)).toHaveCount(1, { timeout: 30_000 });
      }

      const caughtUp = Date.now() - restored;
      report('catch-up', [caughtUp]);

      // Measured from the network returning, not from the next scheduled retry: after two minutes
      // offline the backoff is at 30 s, and the client reconnects on the browser's `online` event
      // rather than waiting it out (chatConnection.ts).
      expect(caughtUp).toBeLessThanOrEqual(CATCH_UP_BUDGET_MS);

      const rendered = await recipient.page.getByTestId('message').allInnerTexts();
      const positions = missed.map((text) => rendered.findIndex((t) => t.includes(text)));
      expect(positions).toEqual([...positions].sort((a, b) => a - b));

      // And live again afterwards.
      const live = await timeDelivery(sender.page, [recipient.page], `after reconnect ${stamp}`);
      expect(live).toBeLessThanOrEqual(P99_BUDGET_MS);
    } finally {
      await sender.context.close();
      await recipient.context.close();
    }
  });
});
