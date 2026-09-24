import { expect, test, type Page } from '@playwright/test';

/**
 * T158 — quickstart V5, image sharing and attachment access control (US5).
 *
 * ⚠️ Written and type-checked; not yet executed against a running stack, the same gap T106, T122,
 * T141, and T219 record for the whole suite (Compose images unbuilt — T013, T027).
 *
 * **Scope note, not a gap to close later.** V5 step 5 (progressive MP4 playback) belongs to US7 and
 * is scripted in `v7-video.spec.ts` alongside the range-request work it depends on, rather than
 * duplicated here. Step 3's EICAR upload *is* scripted, but the assertion that it never becomes
 * retrievable is deliberately thin at this level: the authoritative version runs against a real
 * ClamAV in `tests/Integration/Attachments/MalwareScanTests.cs`, where the verdict, the bucket
 * state, and the refusal can all be inspected. What the browser adds is the half that suite cannot
 * see — that the person is *told*, rather than left watching a spinner.
 *
 * The one thing this file can prove that nothing else can: that a leaked content URL is worthless
 * in a real browser session, cookies, service worker and all.
 */

/** From the development roster. Passwords equal usernames. */
const MEMBER = 'an.nguyen';
const OTHER_MEMBER = 'binh.tran';
const NON_MEMBER = 'chi.le';

/**
 * A minimal but genuine PNG, so the declared content type is not a lie.
 *
 * Inline rather than a fixture file: a committed binary that tests would have to keep in sync with
 * its declared type is a worse dependency than eight lines of base64.
 */
const PNG_BASE64 =
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==';

/**
 * The EICAR test string, assembled at runtime.
 *
 * A source file containing it verbatim is itself flagged by on-access scanners, which would mean
 * this repository could not be checked out on a machine running one.
 */
const EICAR = ['X5O!P%@AP[4\\PZX54(P^)7CC)7}$', 'EICAR-STANDARD-ANTIVIRUS-TEST-FILE', '!$H+H*'].join(
  '',
);

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

/** Pastes an image into the composer, the way a person shares a screenshot. */
async function pasteScreenshot(page: Page): Promise<void> {
  const composer = page.getByTestId('composer-input');
  await composer.click();

  // A synthetic paste carrying a real File. Playwright has no clipboard-image API, and writing the
  // image to the OS clipboard would make the test depend on the host's clipboard daemon.
  await composer.evaluate(async (element, base64: string) => {
    const bytes = Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
    const file = new File([bytes], 'screenshot.png', { type: 'image/png' });

    const transfer = new DataTransfer();
    transfer.items.add(file);

    element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true }));
  }, PNG_BASE64);
}

test.describe('V5 — attachments and access control', () => {
  test('a pasted screenshot uploads, is scanned, and appears inline for every member (step 1)', async ({
    browser,
  }) => {
    const senderContext = await browser.newContext();
    const readerContext = await browser.newContext();

    const sender = await senderContext.newPage();
    const reader = await readerContext.newPage();

    await signIn(sender, MEMBER);
    await signIn(reader, OTHER_MEMBER);

    await openSharedGroup(sender);
    await openSharedGroup(reader);

    await pasteScreenshot(sender);

    // Progress is asserted, not just the outcome. FR-026 requires it, and a silent upload is
    // indistinguishable from a hung one on a slow connection.
    await expect(sender.getByTestId('attachment-upload-progress')).toBeVisible();

    await sender.getByTestId('composer-send').click();

    // "Scanning…" rather than a broken image. FR-024 makes the pending window unavoidable, so the
    // UI has to explain it — this is the assertion that it does.
    await expect(sender.getByText(/scanning/i)).toBeVisible();

    // The scan completes and the preview replaces it, for the sender and for the other member.
    await expect(sender.getByTestId('attachment-image')).toBeVisible({ timeout: 30_000 });
    await expect(reader.getByTestId('attachment-image')).toBeVisible({ timeout: 30_000 });

    await senderContext.close();
    await readerContext.close();
  });

  test('a non-member holding the exact content URL is refused (step 2, SC-017)', async ({
    browser,
  }) => {
    const memberContext = await browser.newContext();
    const outsiderContext = await browser.newContext();

    const member = await memberContext.newPage();
    const outsider = await outsiderContext.newPage();

    await signIn(member, MEMBER);
    await openSharedGroup(member);

    await pasteScreenshot(member);
    await member.getByTestId('composer-send').click();

    const image = member.getByTestId('attachment-image');
    await expect(image).toBeVisible({ timeout: 30_000 });

    const contentUrl = await image.getAttribute('src');
    expect(contentUrl).toBeTruthy();

    // The address is handed over deliberately. FR-025 is not about the URL being unguessable — it
    // is about knowing it exactly buying nothing.
    await signIn(outsider, NON_MEMBER);

    const refused = await outsider.request.get(contentUrl!);

    expect(refused.status()).toBe(404);

    // 404 rather than 403, and that is the requirement rather than an implementation detail: a 403
    // on a real id and a 404 on a fake one is a working enumeration oracle.
    const invented = await outsider.request.get('/api/v1/attachments/' + crypto.randomUUID() + '/content');
    expect(invented.status()).toBe(refused.status());

    await memberContext.close();
    await outsiderContext.close();
  });

  test('the EICAR test file is blocked and the uploader is told why (step 3)', async ({ page }) => {
    await signIn(page, MEMBER);
    await openSharedGroup(page);

    const composer = page.getByTestId('composer-input');
    await composer.click();

    await composer.evaluate(async (element, eicar: string) => {
      const file = new File([eicar], 'invoice.png', { type: 'image/png' });

      const transfer = new DataTransfer();
      transfer.items.add(file);

      element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true }));
    }, EICAR);

    await page.getByTestId('composer-send').click();

    // Told plainly. Silence would look like a failed upload to someone who was expecting the file,
    // and they would retry it indefinitely.
    await expect(page.getByText(/blocked by a malware scan/i)).toBeVisible({ timeout: 60_000 });

    // And never rendered. The scan verdict is the gate; this is the visible consequence of it.
    await expect(page.getByTestId('attachment-image')).toHaveCount(0);
  });

  test('an oversized video is refused before any bytes transfer, with the limit stated (step 4)', async ({
    page,
  }) => {
    await signIn(page, MEMBER);
    await openSharedGroup(page);

    // Every request the page makes while the oversized file is chosen. FR-023 says the rejection
    // happens *before* the upload, so the evidence is the absence of a transfer — not merely an
    // error message that could have appeared after one.
    const uploadAttempts: string[] = [];

    page.on('request', (request) => {
      if (request.method() === 'PUT' || request.url().includes('/attachments')) {
        uploadAttempts.push(`${request.method()} ${request.url()}`);
      }
    });

    const composer = page.getByTestId('composer-input');
    await composer.click();

    await composer.evaluate((element) => {
      // 600 MB declared without allocating it. The client checks the declared size, which is
      // exactly the thing under test.
      const file = new File([], 'long-recording.mp4', { type: 'video/mp4' });
      Object.defineProperty(file, 'size', { value: 600 * 1024 * 1024 });

      const transfer = new DataTransfer();
      transfer.items.add(file);

      element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: transfer, bubbles: true }));
    });

    // The limit is stated. A refusal that does not name it leaves the person guessing how much
    // smaller the file has to be.
    await expect(page.getByText(/500 MB/)).toBeVisible();

    // Nothing was reserved and nothing was uploaded.
    expect(uploadAttempts).toHaveLength(0);
  });

  test('deleting the message stops the attachment being retrievable (step 6, FR-027)', async ({
    page,
  }) => {
    await signIn(page, MEMBER);
    await openSharedGroup(page);

    await pasteScreenshot(page);
    await page.getByTestId('composer-send').click();

    const image = page.getByTestId('attachment-image');
    await expect(image).toBeVisible({ timeout: 30_000 });

    const contentUrl = await image.getAttribute('src');
    expect((await page.request.get(contentUrl!)).status()).toBe(200);

    await page.getByTestId('message-list').getByRole('button', { name: /delete/i }).last().click();
    await page.getByRole('button', { name: /confirm|delete/i }).last().click();

    await expect(page.getByTestId('message-tombstone')).toBeVisible();

    // Same URL, same member, same session. Only the message's state changed.
    expect((await page.request.get(contentUrl!)).status()).toBe(404);
  });
});
