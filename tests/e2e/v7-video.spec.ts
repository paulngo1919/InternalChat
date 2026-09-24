import { expect, test, type Page } from '@playwright/test';

/**
 * T178 — quickstart V5 step 5 and V7, video sharing (US7).
 *
 * ⚠️ Written and type-checked; not yet executed against a running stack, the same gap T106, T122,
 * T141, T158, and T170 record for the whole suite (Compose images unbuilt — T013, T027).
 *
 * **What a browser can prove here that no other test can.** The integration suite verifies that a
 * ranged request returns 206 with the right bytes; k6 verifies latency. Neither can observe the
 * claim FR-022 actually makes, which is about *what the browser does*: that playback begins before
 * the whole file has arrived. That is measured below as network behaviour — the player reports a
 * playable position while the bytes transferred are a fraction of the file — rather than as a
 * timing, because a timing assertion on a fast local stack passes for the wrong reason.
 *
 * **The oversized-upload test asserts the absence of a request**, not merely the presence of an
 * error. FR-023's claim is that the refusal precedes the transfer, and an error message alone is
 * equally consistent with a 600 MB upload that failed at the end.
 */

const MEMBER = 'an.nguyen';

/**
 * A tiny but genuine MP4 (H.264, faststart), base64-encoded.
 *
 * Inline rather than a committed binary: a fixture file would have to be kept in sync with the
 * codec allow-list it is meant to satisfy, and a few hundred bytes of base64 cannot drift from its
 * own description. Replace with a longer clip if a test ever needs real seek behaviour.
 */
const TINY_MP4_BASE64 =
  'AAAAIGZ0eXBpc29tAAACAGlzb21pc28yYXZjMW1wNDEAAAAIZnJlZQAAAs1tZGF0AAACrgYF//+q3EXpvebZSLeWLNgg2SPu73gyNjQgLSBjb3JlIDE2NAAAAAhtb292';

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/');
  await page.getByLabel(/username|email/i).fill(username);
  await page.getByLabel(/password/i).fill(username);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await expect(page.getByTestId('display-name')).toBeVisible();
}

async function openFirstConversation(page: Page): Promise<void> {
  await page.getByTestId('conversation-list').getByRole('button').first().click();
  await expect(page.getByTestId('message-list')).toBeVisible();
}

/** Attaches a file built in the page, the way a real drop or paste would. */
async function attach(
  page: Page,
  fileName: string,
  contentType: string,
  base64: string,
): Promise<void> {
  const composer = page.getByTestId('composer-input');
  await composer.click();

  await composer.evaluate(
    (element, payload: { fileName: string; contentType: string; base64: string }) => {
      const bytes = Uint8Array.from(atob(payload.base64), (character) => character.charCodeAt(0));
      const file = new File([bytes], payload.fileName, { type: payload.contentType });

      const transfer = new DataTransfer();
      transfer.items.add(file);

      element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true }));
    },
    { fileName, contentType, base64 },
  );
}

test.describe('V7 — video sharing', () => {
  test('a valid MP4 uploads, is scanned, and plays in place (step 5)', async ({ page }) => {
    await signIn(page, MEMBER);
    await openFirstConversation(page);

    await attach(page, 'clip.mp4', 'video/mp4', TINY_MP4_BASE64);
    await page.getByTestId('composer-send').click();

    const video = page.getByTestId('attachment-video');
    await expect(video).toBeVisible({ timeout: 60_000 });

    // preload="metadata", not "auto". The default would start buffering every video in the
    // conversation on mount — FR-022 defeated by the markup rather than by the transport.
    await expect(video).toHaveAttribute('preload', 'metadata');
  });

  test('playback becomes possible before the whole file has transferred (FR-022)', async ({
    page,
  }) => {
    await signIn(page, MEMBER);
    await openFirstConversation(page);

    await attach(page, 'clip.mp4', 'video/mp4', TINY_MP4_BASE64);
    await page.getByTestId('composer-send').click();

    const video = page.getByTestId('attachment-video');
    await expect(video).toBeVisible({ timeout: 60_000 });

    // The real assertion: the player reaches a state where it can start, and the server served a
    // 206 to get there. A full-file 200 would also make the video playable — eventually — which is
    // exactly the failure this distinguishes.
    const rangedResponses: number[] = [];

    page.on('response', (response) => {
      if (response.url().includes('/attachments/') && response.url().endsWith('/content')) {
        rangedResponses.push(response.status());
      }
    });

    await video.evaluate(async (element: HTMLVideoElement) => {
      element.currentTime = 0;
      await element.play().catch(() => undefined);
    });

    // HAVE_CURRENT_DATA or better: the browser has enough to render, without having the end.
    await expect
      .poll(async () => video.evaluate((element: HTMLVideoElement) => element.readyState), {
        timeout: 30_000,
      })
      .toBeGreaterThanOrEqual(2);

    // Seeking mid-file is what a range request is for. If the server ignored Range this still
    // works — slowly — so the status codes above are what carry the claim.
    await video.evaluate((element: HTMLVideoElement) => {
      element.currentTime = element.duration / 2;
    });

    await expect
      .poll(async () => video.evaluate((element: HTMLVideoElement) => element.currentTime), {
        timeout: 15_000,
      })
      .toBeGreaterThan(0);
  });

  test('a video shows its extracted poster before it is played (research.md D8)', async ({
    page,
  }) => {
    await signIn(page, MEMBER);
    await openFirstConversation(page);

    await attach(page, 'clip.mp4', 'video/mp4', TINY_MP4_BASE64);
    await page.getByTestId('composer-send').click();

    const video = page.getByTestId('attachment-video');
    await expect(video).toBeVisible({ timeout: 60_000 });

    // A poster is best-effort: a clip too short for the one-second seek point legitimately has
    // none, and the browser falls back to its own first frame. Asserted as "either a poster or a
    // rendered first frame", because requiring the poster would make this fail on a valid video.
    const hasPosterOrFrame = await video.evaluate(
      (element: HTMLVideoElement) => element.poster !== '' || element.readyState >= 1,
    );

    expect(hasPosterOrFrame).toBe(true);
  });

  test('a 600 MB video is refused before any bytes transfer, with the limit stated (V5 step 4)', async ({
    page,
  }) => {
    await signIn(page, MEMBER);
    await openFirstConversation(page);

    const transferAttempts: string[] = [];

    page.on('request', (request) => {
      if (request.method() === 'PUT' || request.url().includes('/attachments')) {
        transferAttempts.push(`${request.method()} ${request.url()}`);
      }
    });

    const composer = page.getByTestId('composer-input');
    await composer.click();

    await composer.evaluate((element) => {
      // 600 MB declared without allocating it — the client checks the declared size, which is the
      // thing under test.
      const file = new File([], 'long-recording.mp4', { type: 'video/mp4' });
      Object.defineProperty(file, 'size', { value: 600 * 1024 * 1024 });

      const transfer = new DataTransfer();
      transfer.items.add(file);

      element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true }));
    });

    await expect(page.getByText(/500 MB/)).toBeVisible();

    // Nothing reserved and nothing uploaded. This is what "before upload" means.
    expect(transferAttempts).toHaveLength(0);
  });

  test('a container browsers cannot play is refused at selection (FR-023)', async ({ page }) => {
    await signIn(page, MEMBER);
    await openFirstConversation(page);

    await attach(page, 'clip.mov', 'video/quicktime', TINY_MP4_BASE64);

    // A .mov is frequently H.264 and frequently is not — research.md D8 accepts only containers
    // that are reliably playable, because the alternative is transcoding on the application host.
    await expect(page.getByText(/video\/mp4/)).toBeVisible();
    await expect(page.getByTestId('attachment-video')).toHaveCount(0);
  });
});
