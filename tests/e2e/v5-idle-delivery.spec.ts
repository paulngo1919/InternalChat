import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';

/**
 * 002 T011 / T037 — delivery after a quiet period, and on a degraded connection.
 *
 * **Idle then send (002 FR-002, SC-006).** The reported delay was worst exactly when nobody was
 * chatting: an idle dispatcher, cold database connections, and a first message that paid for all of
 * it. This waits until the platform has been quiet for `IDLE_MS`, sends one message, and holds it to
 * the same budget as a message sent mid-conversation.
 *
 *   IDLE_MS   CI default 12 s — more than twice the 5 s backstop poll, so a fix that only shortened
 *             the poll would still be caught. The nightly job sets 600000 (the spec's 10 minutes),
 *             which also outlasts Npgsql's idle-connection pruning.
 *
 * **Long polling (002 FR-010, SC-005).** WebSocket upgrades to the hub are refused, SignalR falls
 * back, and the employee is told their connection is limited while messages still arrive within a
 * second.
 */

const SENDER = 'an.nguyen';
const RECIPIENT = 'binh.tran';

const IDLE_MS = Number(process.env['IDLE_MS'] ?? 12_000);
const TRIALS = 5;

/** 002 SC-001 p99 — the first message after idle is held to the steady-state budget. */
const IDLE_BUDGET_MS = 500;

/** 002 SC-005 — long polling, p95. */
const LONG_POLLING_P95_MS = 1_000;

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/');
  await page.getByLabel(/username|email/i).fill(username);
  await page.getByLabel(/password/i).fill(username);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await expect(page.getByTestId('display-name')).toBeVisible();
}

async function openAs(
  browser: Browser,
  username: string,
  prepare?: (context: BrowserContext) => Promise<void>,
): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext();
  await prepare?.(context);
  const page = await context.newPage();
  await signIn(page, username);
  await page.getByTestId('conversation-item').first().click();
  await expect(page.getByTestId('composer')).toBeVisible();
  return { context, page };
}

async function timeDelivery(sender: Page, recipient: Page, text: string): Promise<number> {
  const started = Date.now();
  await sender.getByTestId('composer').fill(text);
  await sender.getByTestId('composer').press('Enter');
  await recipient.getByText(text).waitFor({ state: 'visible', timeout: 15_000 });
  return Date.now() - started;
}

function percentile(samples: number[], p: number): number {
  const sorted = [...samples].sort((a, b) => a - b);
  const rank = Math.ceil((p / 100) * sorted.length) - 1;
  return sorted[Math.min(Math.max(rank, 0), sorted.length - 1)] ?? 0;
}

test.describe('V5 — delivery after idle, and on a degraded connection', () => {
  test('the first message after a quiet period is as fast as any other', async ({ browser }) => {
    test.setTimeout(TRIALS * (IDLE_MS + 60_000));

    const sender = await openAs(browser, SENDER);
    const recipient = await openAs(browser, RECIPIENT);

    try {
      const durations: number[] = [];

      for (let i = 0; i < TRIALS; i++) {
        // Quiet: nothing sent by anyone in this run for IDLE_MS. The seeded stack has no other
        // traffic, so this is the platform's idle state, not merely this conversation's.
        await sender.page.waitForTimeout(IDLE_MS);
        durations.push(await timeDelivery(sender.page, recipient.page, `after idle ${String(i)} ${String(Date.now())}`));
      }

      // eslint-disable-next-line no-console -- the measurement is the point of this test.
      console.log(`after ${String(IDLE_MS)} ms idle: ${durations.map(String).join(', ')} ms`);

      for (const [i, duration] of durations.entries()) {
        expect(
          duration,
          `Trial ${String(i)}: the first message after ${String(IDLE_MS)} ms idle took ${String(duration)} ms (002 FR-002).`,
        ).toBeLessThanOrEqual(IDLE_BUDGET_MS);
      }
    } finally {
      await sender.context.close();
      await recipient.context.close();
    }
  });

  test.describe('long polling', () => {
    test('a client without WebSockets is told, and still receives within a second', async ({ browser }) => {
      const sender = await openAs(browser, SENDER);

      // Refuse the WebSocket upgrade for the hub only. SignalR negotiates, fails the socket, and
      // falls back to long polling — the path a restrictive office proxy forces.
      const recipient = await openAs(browser, RECIPIENT, async (context) => {
        await context.routeWebSocket(/\/hubs\/chat/, (ws) => {
          ws.close({ code: 1008, reason: 'WebSockets blocked by test' });
        });
      });

      try {
        await expect(recipient.page.getByTestId('connection-degraded')).toBeVisible({ timeout: 30_000 });

        const durations: number[] = [];

        for (let i = 0; i < 20; i++) {
          await sender.page.waitForTimeout(Math.floor(Math.random() * 1_000));
          durations.push(await timeDelivery(sender.page, recipient.page, `long poll ${String(i)} ${String(Date.now())}`));
        }

        const p95 = percentile(durations, 95);
        // eslint-disable-next-line no-console -- the measurement is the point of this test.
        console.log(`long polling: p50 ${String(percentile(durations, 50))} ms, p95 ${String(p95)} ms`);

        expect(p95).toBeLessThanOrEqual(LONG_POLLING_P95_MS);

        // The sender, on WebSockets, is not told anything.
        await expect(sender.page.getByTestId('connection-degraded')).toHaveCount(0);
      } finally {
        await sender.context.close();
        await recipient.context.close();
      }
    });
  });
});
