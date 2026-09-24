import { expect, test, type Page } from '@playwright/test';

/**
 * T122 — quickstart V3, groups and membership revocation (US3).
 *
 * ⚠️ Written and type-checked; not yet executed against a running stack, for the same reason
 * v2-delivery-timing.spec.ts records: it needs the Compose images, which T013 and T027 note are not
 * yet built. T219 is where the whole quickstart runs.
 *
 * `workers: 1` and `retries: 0` (playwright.config.ts) apply here too: a retried run would create a
 * second group with the same name, and two workers adding and removing the same roster members would
 * race each other's membership changes.
 */

/**
 * From the development roster (tools/Seeder/DevelopmentSeeder.cs, deploy/keycloak/README.md).
 * Passwords equal usernames; display names are what the UI actually renders, since the directory
 * search and member list show `displayName`, never the username.
 */
const ADMIN = { username: 'an.nguyen', displayName: 'Nguyễn Thị Vân An' };
const STAYS_IN = { username: 'binh.tran', displayName: 'Trần Quốc Bình' };
const REMOVED = { username: 'chi.le', displayName: 'Lê Bảo Chi' };
const ADDED_LATER = { username: 'dung.pham', displayName: 'Phạm Tiến Dũng' };

async function signIn(page: Page, username: string): Promise<void> {
  await page.goto('/');
  await page.getByLabel(/username|email/i).fill(username);
  await page.getByLabel(/password/i).fill(username);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await expect(page.getByTestId('display-name')).toBeVisible();
}

/** Searches the people picker and adds one result, by the display name it renders. */
async function findAndAdd(page: Page, searchLabel: string, displayName: string): Promise<void> {
  await page.getByLabel(searchLabel).fill(displayName);
  await page.getByRole('button', { name: 'Search' }).click();
  await page
    .getByRole('listitem')
    .filter({ hasText: displayName })
    .getByRole('button', { name: 'Add' })
    .click();
}

async function createGroup(
  page: Page,
  name: string,
  members: readonly { readonly displayName: string }[],
): Promise<void> {
  await page.getByRole('button', { name: 'New group' }).click();
  await page.getByLabel('Group name').fill(name);

  for (const member of members) {
    await findAndAdd(page, 'Find colleagues to add', member.displayName);
  }

  await page.getByRole('button', { name: 'Create group' }).click();
}

test.describe('V3 — groups and membership revocation', () => {
  test('creating a group with members shows it to them without refreshing (scenario 1)', async ({
    browser,
  }) => {
    const adminContext = await browser.newContext();
    const staysInContext = await browser.newContext();

    const admin = await adminContext.newPage();
    const staysIn = await staysInContext.newPage();

    try {
      // The recipient is already signed in and watching their conversation list *before* the group
      // exists — the point of this test is the list updating on its own, not on their next load.
      await signIn(staysIn, STAYS_IN.username);
      await signIn(admin, ADMIN.username);

      const groupName = `v3 scenario 1 ${String(Date.now())}`;
      await createGroup(admin, groupName, [STAYS_IN]);

      await expect(staysIn.getByText(groupName)).toBeVisible({ timeout: 10_000 });

      await admin.getByTestId('composer').fill('welcome to the group');
      await admin.getByTestId('composer').press('Enter');

      await staysIn.getByText(groupName).click();
      await expect(staysIn.getByText('welcome to the group')).toBeVisible();
    } finally {
      await adminContext.close();
      await staysInContext.close();
    }
  });

  test('a member added later sees history only from their join point, and the rule is shown (scenario 4)', async ({
    browser,
  }) => {
    const adminContext = await browser.newContext();
    const addedLaterContext = await browser.newContext();

    const admin = await adminContext.newPage();
    const addedLater = await addedLaterContext.newPage();

    try {
      await signIn(admin, ADMIN.username);

      const groupName = `v3 scenario 4 ${String(Date.now())}`;
      await createGroup(admin, groupName, [STAYS_IN]);

      await admin.getByTestId('composer').fill('sent before you joined');
      await admin.getByTestId('composer').press('Enter');

      // From-join is the default: history-visibility is not offered as a control on the create
      // form (openapi.yaml defaults it), so this exercises the common case rather than an edge one.
      await findAndAdd(admin, 'Add a member', ADDED_LATER.displayName);

      await signIn(addedLater, ADDED_LATER.username);
      await addedLater.getByText(groupName).click();

      // The rule is displayed, and the message sent before joining is not.
      await expect(addedLater.getByTestId('history-notice')).toContainText('after they joined');
      await expect(addedLater.getByText('sent before you joined')).toHaveCount(0);

      await admin.getByTestId('composer').fill('sent after you joined');
      await admin.getByTestId('composer').press('Enter');

      await expect(addedLater.getByText('sent after you joined')).toBeVisible({ timeout: 10_000 });
    } finally {
      await adminContext.close();
      await addedLaterContext.close();
    }
  });

  test('a removed member stops receiving immediately, without refreshing (scenario 3)', async ({
    browser,
  }) => {
    const adminContext = await browser.newContext();
    const removedContext = await browser.newContext();

    const admin = await adminContext.newPage();
    const removed = await removedContext.newPage();

    try {
      await signIn(admin, ADMIN.username);
      await signIn(removed, REMOVED.username);

      const groupName = `v3 scenario 3 ${String(Date.now())}`;
      await createGroup(admin, groupName, [REMOVED]);

      await expect(removed.getByText(groupName)).toBeVisible({ timeout: 10_000 });
      await removed.getByText(groupName).click();
      await expect(removed.getByTestId('composer')).toBeVisible();

      // The member being removed is the one whose window is open, deliberately — the hub filter
      // that closes an idle connection cannot be what proves this; the connection here is active.
      await admin.getByText(groupName).click();
      await admin
        .getByTestId('group-member')
        .filter({ hasText: REMOVED.displayName })
        .getByRole('button', { name: 'Remove' })
        .click();

      // Gone from the conversation view without the removed member doing anything — no reload, no
      // click. contracts/signalr-hub.md: "Server moves the connection ... out of the group
      // immediately."
      await expect(removed.getByText('Choose a conversation.')).toBeVisible({ timeout: 10_000 });
      await expect(removed.getByText(groupName)).toHaveCount(0);

      await admin.getByTestId('composer').fill('posted after the removal');
      await admin.getByTestId('composer').press('Enter');

      // Never appears for the removed member, even after the message that would have followed it.
      await expect(removed.getByText('posted after the removal')).toHaveCount(0);
    } finally {
      await adminContext.close();
      await removedContext.close();
    }
  });
});
