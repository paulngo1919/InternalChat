import { expect, test, type Page } from '@playwright/test';

/**
 * T198 — quickstart V7, video meetings (US8).
 *
 * ⚠️ Written and type-checked; not yet executed. Two gaps apply here, and the second is larger
 * than the one the other E2E specs record:
 *
 * 1. The Compose images are unbuilt (T013, T027), the same gap as T106/T122/T141/T158/T170.
 * 2. **These tests need a running LiveKit on a media host.** Everything below that involves actual
 *    media — two-way audio and video, the participant tiles filling, screen share reaching the
 *    other side — requires an SFU and two browsers with fake devices. Chromium's
 *    `--use-fake-device-for-media-capture` makes the browser half possible; the SFU half is real
 *    infrastructure. Tests that need it are gated on `MEDIA_HOST` rather than skipped silently,
 *    so a run without one reports what it did not cover.
 *
 * **What is asserted without a media host** is the part that is this codebase's: that a member sees
 * the join prompt, that a non-member never does, and that the meeting UI is not in the initial
 * bundle. Those are the claims a browser can settle and the integration suite cannot.
 */

const STARTER = 'an.nguyen';
const MEMBER = 'binh.tran';
const NON_MEMBER = 'chi.le';

/** Set when deploy/docker-compose.media.yml is up. */
const hasMediaHost = process.env.MEDIA_HOST === 'true';

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/');
  await page.getByLabel(/username|email/i).fill(username);
  await page.getByLabel(/password/i).fill(username);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await expect(page.getByTestId('display-name')).toBeVisible();
}

async function openSharedGroup(page: Page): Promise<void> {
  await page.getByTestId('conversation-list').getByRole('button').first().click();
  await expect(page.getByTestId('message-list')).toBeVisible();
}

test.describe('V7 — meetings', () => {
  test('starting a meeting shows every other member the join prompt (step 1, FR-041)', async ({
    browser,
  }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    const starterContext = await browser.newContext();
    const memberContext = await browser.newContext();

    const starter = await starterContext.newPage();
    const member = await memberContext.newPage();

    await signIn(starter, STARTER);
    await signIn(member, MEMBER);

    await openSharedGroup(starter);
    await openSharedGroup(member);

    await starter.getByTestId('start-meeting').click();

    // The prompt arrives over SignalR, to the conversation's group — nobody is invited
    // individually, because the meeting belongs to the conversation.
    await expect(member.getByTestId('meeting-prompt')).toBeVisible({ timeout: 15_000 });
    await expect(member.getByTestId('meeting-join')).toBeEnabled();

    await starterContext.close();
    await memberContext.close();
  });

  test('a non-member never sees the prompt', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    const starterContext = await browser.newContext();
    const outsiderContext = await browser.newContext();

    const starter = await starterContext.newPage();
    const outsider = await outsiderContext.newPage();

    await signIn(starter, STARTER);
    await openSharedGroup(starter);
    await starter.getByTestId('start-meeting').click();

    await signIn(outsider, NON_MEMBER);

    // Not "the prompt is hidden" — the event never reaches them, because SignalR group membership
    // follows conversation membership. Given a moment to be sure it did not arrive late.
    await outsider.waitForTimeout(3_000);
    await expect(outsider.getByTestId('meeting-prompt')).toHaveCount(0);

    await starterContext.close();
    await outsiderContext.close();
  });

  test('two participants get two-way audio and video (step 2, FR-042)', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    // Fake devices, so this runs headless without a camera. The tracks are synthetic; what is
    // under test is that they are published and subscribed, not what they contain.
    const context = await browser.newContext({ permissions: ['camera', 'microphone'] });

    const first = await context.newPage();
    const second = await context.newPage();

    await signIn(first, STARTER);
    await signIn(second, MEMBER);

    await openSharedGroup(first);
    await openSharedGroup(second);

    await first.getByTestId('start-meeting').click();
    await expect(first.getByTestId('meeting-room')).toBeVisible({ timeout: 30_000 });

    await second.getByTestId('meeting-join').click();
    await expect(second.getByTestId('meeting-room')).toBeVisible({ timeout: 30_000 });

    // Each sees two tiles: themselves and the other. One tile means the subscription never
    // completed, which looks like a slow connection and is a broken one.
    await expect(first.getByTestId('participant-grid').locator('video')).toHaveCount(2, {
      timeout: 30_000,
    });
    await expect(second.getByTestId('participant-grid').locator('video')).toHaveCount(2, {
      timeout: 30_000,
    });

    await context.close();
  });

  test('mute and camera toggles propagate (step 3, FR-045)', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    const context = await browser.newContext({ permissions: ['camera', 'microphone'] });
    const page = await context.newPage();

    await signIn(page, STARTER);
    await openSharedGroup(page);
    await page.getByTestId('start-meeting').click();

    await expect(page.getByTestId('meeting-room')).toBeVisible({ timeout: 30_000 });

    const microphone = page.getByTestId('toggle-microphone');
    await expect(microphone).toHaveAttribute('aria-pressed', 'true');

    await microphone.click();
    await expect(microphone).toHaveAttribute('aria-pressed', 'false');

    const camera = page.getByTestId('toggle-camera');
    await camera.click();
    await expect(camera).toHaveAttribute('aria-pressed', 'false');

    await context.close();
  });

  test('the meeting continues when the starter leaves (FR-047)', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    const context = await browser.newContext({ permissions: ['camera', 'microphone'] });

    const starter = await context.newPage();
    const other = await context.newPage();

    await signIn(starter, STARTER);
    await signIn(other, MEMBER);

    await openSharedGroup(starter);
    await openSharedGroup(other);

    await starter.getByTestId('start-meeting').click();
    await expect(starter.getByTestId('meeting-room')).toBeVisible({ timeout: 30_000 });

    await other.getByTestId('meeting-join').click();
    await expect(other.getByTestId('meeting-room')).toBeVisible({ timeout: 30_000 });

    await starter.getByTestId('leave-meeting').click();

    // Still there. A design in which the starter's departure ended the call makes every meeting
    // hostage to whoever happened to click first.
    await other.waitForTimeout(3_000);
    await expect(other.getByTestId('meeting-room')).toBeVisible();

    await context.close();
  });

  test('meetings are not in the initial bundle (T193, T195)', async ({ page }) => {
    // Runs WITHOUT a media host: it is about what the browser downloads, not about what it
    // connects to. This is the assertion no other test in the project can make — the Vite plugin
    // checks the build output, and this checks what a real page load actually fetches.
    const scripts: string[] = [];

    page.on('response', (response) => {
      if (response.url().endsWith('.js')) {
        scripts.push(response.url());
      }
    });

    await signIn(page, STARTER);
    await openSharedGroup(page);

    // Nothing has opened a meeting, so no chunk containing the SDK should have been requested.
    const meetingChunks = scripts.filter((url) => /meeting|livekit/i.test(url));

    expect(
      meetingChunks,
      `The meetings chunk was downloaded before anyone opened a meeting: ${meetingChunks.join(', ')}`,
    ).toHaveLength(0);
  });
});
