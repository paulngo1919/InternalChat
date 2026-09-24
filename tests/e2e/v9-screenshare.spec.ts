import { expect, test, type Page } from '@playwright/test';

/**
 * T205 — quickstart V7 screen-share scenarios (US9).
 *
 * ⚠️ Written and type-checked; not executed. Same two gaps as T198: the Compose images are unbuilt,
 * and everything involving real media needs a running LiveKit. Media-dependent tests are gated on
 * `MEDIA_HOST=true` rather than skipped silently, so a run without one reports what it missed.
 *
 * **One scenario is deliberately NOT automated, and the reason is not effort.** Quickstart V7 step
 * 4 asks the tester to share a single application window, switch applications, and confirm the
 * other application is not revealed. That is a claim about the operating system's window capture,
 * and no browser automation can verify it: Playwright cannot drive the OS picker, cannot alt-tab
 * between real applications, and cannot see what a capture would have contained. Faking it —
 * `getDisplayMedia` with a preferred surface and an assertion about the track's settings — would
 * assert that Chromium reports what we asked for, not that the guarantee holds.
 *
 * FR-048's window promise is enforced by the platform (a single-window capture never receives
 * frames from anything outside it), and it is verified by a human in T219. That is recorded here
 * rather than left as an apparent gap in coverage.
 */

const PRESENTER = 'an.nguyen';
const SECOND = 'binh.tran';
const VIEWER = 'chi.le';

const hasMediaHost = process.env.MEDIA_HOST === 'true';

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/');
  await page.getByLabel(/username|email/i).fill(username);
  await page.getByLabel(/password/i).fill(username);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await expect(page.getByTestId('display-name')).toBeVisible();
}

async function joinMeeting(page: Page, start: boolean): Promise<void> {
  await page.getByTestId('conversation-list').getByRole('button').first().click();
  await expect(page.getByTestId('message-list')).toBeVisible();

  if (start) {
    await page.getByTestId('start-meeting').click();
  } else {
    await page.getByTestId('meeting-join').click();
  }

  await expect(page.getByTestId('meeting-room')).toBeVisible({ timeout: 30_000 });
}

test.describe('V7 — screen sharing', () => {
  test('a shared screen reaches the other participants (step 4, FR-048)', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    // --use-fake-ui-for-media-stream auto-accepts the picker; --auto-select-desktop-capture-source
    // picks a surface without a human. Both are Chromium flags configured in playwright.config.ts
    // for this project — without them getDisplayMedia blocks forever in headless.
    const context = await browser.newContext({ permissions: ['camera', 'microphone'] });

    const presenter = await context.newPage();
    const viewer = await context.newPage();

    await signIn(presenter, PRESENTER);
    await signIn(viewer, SECOND);

    await joinMeeting(presenter, true);
    await joinMeeting(viewer, false);

    await presenter.getByTestId('toggle-screen-share').click();

    await expect(presenter.getByTestId('toggle-screen-share')).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    // The viewer sees it, and is told whose it is — FR-050 from the receiving side.
    await expect(viewer.getByTestId('shared-view')).toBeVisible({ timeout: 30_000 });
    await expect(viewer.getByTestId('share-presenter')).toContainText(/screen/i);

    await context.close();
  });

  test('a second sharer takes over and the first is told why (FR-050)', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    const context = await browser.newContext({ permissions: ['camera', 'microphone'] });

    const first = await context.newPage();
    const second = await context.newPage();

    await signIn(first, PRESENTER);
    await signIn(second, SECOND);

    await joinMeeting(first, true);
    await joinMeeting(second, false);

    await first.getByTestId('toggle-screen-share').click();
    await expect(first.getByTestId('toggle-screen-share')).toHaveAttribute('aria-pressed', 'true');

    await second.getByTestId('toggle-screen-share').click();

    // The rule, applied: last writer wins.
    await expect(second.getByTestId('toggle-screen-share')).toHaveAttribute(
      'aria-pressed',
      'true',
      { timeout: 15_000 },
    );

    // The rule, made VISIBLE. This is the assertion FR-050 is actually about — without it the
    // first presenter's share vanishes and they cannot tell a takeover from a dropped connection.
    await expect(first.getByTestId('share-displaced')).toBeVisible({ timeout: 15_000 });
    await expect(first.getByTestId('toggle-screen-share')).toHaveAttribute('aria-pressed', 'false');

    await context.close();
  });

  test('exactly one share is visible after a takeover', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    const context = await browser.newContext({ permissions: ['camera', 'microphone'] });

    const first = await context.newPage();
    const second = await context.newPage();
    const viewer = await context.newPage();

    await signIn(first, PRESENTER);
    await signIn(second, SECOND);
    await signIn(viewer, VIEWER);

    await joinMeeting(first, true);
    await joinMeeting(second, false);
    await joinMeeting(viewer, false);

    await first.getByTestId('toggle-screen-share').click();
    await expect(viewer.getByTestId('shared-view')).toBeVisible({ timeout: 30_000 });

    await second.getByTestId('toggle-screen-share').click();

    // One frame, not two. A viewer seeing both is the state FR-050 forbids, and it is the outcome
    // if the server-side single-publisher rule is ever removed — the browser and the SFU will both
    // happily carry two.
    await expect(viewer.getByTestId('shared-view')).toHaveCount(1, { timeout: 15_000 });

    await context.close();
  });

  test('a shared screen can be enlarged for legibility (FR-049)', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    const context = await browser.newContext({ permissions: ['camera', 'microphone'] });

    const presenter = await context.newPage();
    const viewer = await context.newPage();

    await signIn(presenter, PRESENTER);
    await signIn(viewer, SECOND);

    await joinMeeting(presenter, true);
    await joinMeeting(viewer, false);

    await presenter.getByTestId('toggle-screen-share').click();
    await expect(viewer.getByTestId('shared-view')).toBeVisible({ timeout: 30_000 });

    const enlarge = viewer.getByTestId('toggle-enlarge-share');
    await enlarge.click();

    // FR-049 is partly a media question and partly a layout one: a 1080p document tiled beside
    // five faces in a narrow column is unreadable however good the stream is.
    await expect(enlarge).toHaveAttribute('aria-pressed', 'true');
    await expect(viewer.getByTestId('shared-view')).toHaveClass(/is-enlarged/);

    // Escape leaves it, matching every other full-bleed surface in the app.
    await viewer.keyboard.press('Escape');
    await expect(enlarge).toHaveAttribute('aria-pressed', 'false');

    await context.close();
  });

  test('stopping a share removes it for everyone', async ({ browser }) => {
    test.skip(!hasMediaHost, 'Needs a running LiveKit; set MEDIA_HOST=true.');

    const context = await browser.newContext({ permissions: ['camera', 'microphone'] });

    const presenter = await context.newPage();
    const viewer = await context.newPage();

    await signIn(presenter, PRESENTER);
    await signIn(viewer, SECOND);

    await joinMeeting(presenter, true);
    await joinMeeting(viewer, false);

    await presenter.getByTestId('toggle-screen-share').click();
    await expect(viewer.getByTestId('shared-view')).toBeVisible({ timeout: 30_000 });

    await presenter.getByTestId('toggle-screen-share').click();

    await expect(viewer.getByTestId('shared-view')).toHaveCount(0, { timeout: 15_000 });

    await context.close();
  });
});
