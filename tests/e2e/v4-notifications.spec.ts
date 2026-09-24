import { expect, test, type Page } from '@playwright/test';

/**
 * T141 — quickstart V4, notifications and unread state (US4).
 *
 * ⚠️ Written and type-checked; not yet executed against a running stack, the same gap T106, T122,
 * and T219 record for the whole suite (Compose images unbuilt — T013, T027).
 *
 * **Scope note, not a gap to close later.** Quickstart V4 steps 1, 2, and the interruption half of
 * step 4 all require a real OS-level push notification to actually arrive — which needs a genuine
 * browser vendor push service round trip that Playwright has no API to observe (there is no
 * "assert an OS notification appeared" call). Simulating it by calling the service worker's
 * `showNotification` directly would test Playwright's own script, not this platform. What *is*
 * genuinely observable through the UI is scripted below: FR-040's warning (step 5, minus actually
 * toggling a real browser permission, which needs the same unavailable API), cross-device unread
 * clearing (step 3), and that the DND settings UI actually persists a change (the reachable half of
 * step 4). T219 is where the full quickstart, including the push-delivery steps, runs by hand.
 */

/** From the development roster. Passwords equal usernames. */
const SENDER = 'an.nguyen';
const READER = 'binh.tran';

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/');
  await page.getByLabel(/username|email/i).fill(username);
  await page.getByLabel(/password/i).fill(username);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await expect(page.getByTestId('display-name')).toBeVisible();
}

test.describe('V4 — notifications and unread state', () => {
  test('an employee with no push subscription is told plainly, not left to assume they are reachable (FR-040)', async ({
    page,
  }) => {
    // A freshly seeded employee has registered no push subscription with the server regardless of
    // this browser's own permission state, so the warning here is driven by the half of FR-040
    // the server can actually attest to.
    await signIn(page, READER);

    await expect(page.getByTestId('notification-capability-warning')).toBeVisible();
  });

  test('reading a conversation on one device clears the unread badge on another (scenario 3)', async ({
    browser,
  }) => {
    const senderContext = await browser.newContext();
    const readerDeviceOne = await browser.newContext();
    const readerDeviceTwo = await browser.newContext();

    const sender = await senderContext.newPage();
    const deviceOne = await readerDeviceOne.newPage();
    const deviceTwo = await readerDeviceTwo.newPage();

    try {
      await signIn(sender, SENDER);
      await signIn(deviceOne, READER);
      await signIn(deviceTwo, READER);

      const text = `unread badge probe ${String(Date.now())}`;

      await sender.getByTestId('conversation-item').first().click();
      await sender.getByTestId('composer').fill(text);
      await sender.getByTestId('composer').press('Enter');

      // ConversationList renders the count as a bare number and puts the accessible description
      // ("N unread") on `aria-label` (WCAG 1.4.1) — matched here by that attribute rather than
      // visible text, which is just the digit.
      const unreadBadge = deviceTwo.getByTestId('conversation-item').first().locator('[aria-label$="unread"]');
      await expect(unreadBadge).toBeVisible({ timeout: 10_000 });

      // Reading on device one — SignalR delivers the read state to device two without a reload.
      await deviceOne.getByTestId('conversation-item').first().click();
      await expect(deviceOne.getByText(text)).toBeVisible();

      await expect(unreadBadge).toHaveCount(0, { timeout: 10_000 });
    } finally {
      await senderContext.close();
      await readerDeviceOne.close();
      await readerDeviceTwo.close();
    }
  });

  test('a do-not-disturb window can be set and is still in effect after a reload (scenario 4, settings half)', async ({
    page,
  }) => {
    await signIn(page, SENDER);

    await page.getByLabel('Do not disturb').check();
    await page.getByLabel('From').fill('22:00');
    await page.getByLabel('To').fill('07:00');
    await page.getByRole('button', { name: 'Save' }).click();

    await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled();

    await page.reload();

    await expect(page.getByLabel('Do not disturb')).toBeChecked();
    await expect(page.getByLabel('From')).toHaveValue('22:00');
    await expect(page.getByLabel('To')).toHaveValue('07:00');
  });
});
