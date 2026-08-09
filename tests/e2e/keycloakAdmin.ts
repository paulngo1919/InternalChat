/**
 * Minimal Keycloak admin client for the end-to-end scenarios.
 *
 * Only what quickstart V1 needs: find a user, disable them, enable them again. Deliberately not a
 * general-purpose wrapper — an admin client with more reach than the tests require is a larger
 * blast radius on a stack somebody may point at something real by accident.
 */

const KEYCLOAK_URL = process.env['E2E_KEYCLOAK_URL'] ?? 'http://localhost:8082';
const REALM = process.env['E2E_REALM'] ?? 'internalchat';
const ADMIN_USER = process.env['E2E_KEYCLOAK_ADMIN'] ?? 'admin';
const ADMIN_PASSWORD = process.env['E2E_KEYCLOAK_ADMIN_PASSWORD'] ?? 'change-me-keycloak';

async function adminToken(): Promise<string> {
  const response = await fetch(`${KEYCLOAK_URL}/realms/master/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'password',
      client_id: 'admin-cli',
      username: ADMIN_USER,
      password: ADMIN_PASSWORD,
    }).toString(),
  });

  if (!response.ok) {
    throw new Error(
      `Could not obtain a Keycloak admin token (${String(response.status)}). ` +
        'Set E2E_KEYCLOAK_ADMIN_PASSWORD to the value in deploy/.env.',
    );
  }

  const payload = (await response.json()) as { access_token: string };
  return payload.access_token;
}

/**
 * The realm user id for a username.
 *
 * The development realm sets these to `dev-{username}` (deploy/keycloak/realm-export.json), but the
 * id is looked up rather than assumed — a test that hardcoded it would pass against the dev realm
 * and silently target nothing anywhere else.
 */
export async function findUserId(username: string): Promise<string> {
  const token = await adminToken();

  const response = await fetch(
    `${KEYCLOAK_URL}/admin/realms/${REALM}/users?username=${encodeURIComponent(username)}&exact=true`,
    { headers: { Authorization: `Bearer ${token}` } },
  );

  const users = (await response.json()) as { id: string }[];
  const user = users[0];

  if (!user) {
    throw new Error(`Realm '${REALM}' has no user '${username}'. Is the realm imported?`);
  }

  return user.id;
}

/**
 * Enables or disables a realm user.
 *
 * Disabling is how quickstart V1 step 3 is performed. Keycloak ends the user's sessions and calls
 * this platform's back-channel logout endpoint, which writes the Redis revocation set — the same
 * path a real departure takes.
 */
export async function setUserEnabled(username: string, enabled: boolean): Promise<void> {
  const token = await adminToken();
  const id = await findUserId(username);

  const response = await fetch(`${KEYCLOAK_URL}/admin/realms/${REALM}/users/${id}`, {
    method: 'PUT',
    headers: {
      Authorization: `Bearer ${token}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ enabled }),
  });

  if (!response.ok) {
    throw new Error(
      `Could not set enabled=${String(enabled)} on '${username}' (${String(response.status)}).`,
    );
  }
}

/** Ends every session a user holds, without disabling the account. */
export async function logoutAllSessions(username: string): Promise<void> {
  const token = await adminToken();
  const id = await findUserId(username);

  await fetch(`${KEYCLOAK_URL}/admin/realms/${REALM}/users/${id}/logout`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}` },
  });
}
