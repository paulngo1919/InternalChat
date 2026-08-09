/**
 * T073 — OIDC authorization code with PKCE against Keycloak.
 *
 * research.md D5. The SPA is a public client: it cannot hold a secret, so PKCE is what stops an
 * authorization code intercepted on the redirect from being redeemed by anyone else.
 */

import { createPkcePair, expiryOf, randomString } from './pkce'

/** Where the authorization server lives and what this client is called. */
export interface OidcConfig {
  /** Realm URL, e.g. `http://localhost:8082/realms/internalchat`. */
  readonly authority: string
  /** Public client id from the realm export. */
  readonly clientId: string
  /** Must exactly match a redirect URI registered on the client. */
  readonly redirectUri: string
  /** Where to land after sign-out. */
  readonly postLogoutRedirectUri: string
  /** Requested scopes. `openid` is mandatory. */
  readonly scope: string
}

/** A token set as returned by the token endpoint, with expiry resolved to a timestamp. */
export interface TokenSet {
  readonly accessToken: string
  readonly refreshToken: string | null
  readonly idToken: string | null
  /** Milliseconds since the epoch. Read from the token, not from `expires_in`. */
  readonly expiresAt: number
}

/** Raw token endpoint response. */
interface TokenResponse {
  access_token: string
  refresh_token?: string
  id_token?: string
  expires_in?: number
}

/**
 * Where the in-flight authorization request is kept.
 *
 * `sessionStorage`, not `localStorage`: the verifier and state belong to one tab's sign-in attempt
 * and are worthless afterwards. `localStorage` would share them across tabs and outlive the browser
 * session, which is a longer life than a single-use secret should have.
 */
const VERIFIER_KEY = 'internalchat.pkce.verifier'
const STATE_KEY = 'internalchat.pkce.state'
const RETURN_TO_KEY = 'internalchat.returnTo'

/** Endpoints derived from the authority, per the OIDC discovery layout Keycloak uses. */
function endpoints(config: OidcConfig) {
  const base = config.authority.replace(/\/$/, '')

  return {
    authorize: `${base}/protocol/openid-connect/auth`,
    token: `${base}/protocol/openid-connect/token`,
    endSession: `${base}/protocol/openid-connect/logout`,
  }
}

/**
 * Starts the flow by redirecting to the authorization endpoint.
 *
 * @param returnTo Path to restore after sign-in, so an employee who deep-linked lands where they
 *   meant to rather than on the conversation list.
 */
export async function beginSignIn(config: OidcConfig, returnTo?: string): Promise<void> {
  const { verifier, challenge } = await createPkcePair()
  const state = randomString(32)

  sessionStorage.setItem(VERIFIER_KEY, verifier)
  sessionStorage.setItem(STATE_KEY, state)

  if (returnTo) {
    sessionStorage.setItem(RETURN_TO_KEY, returnTo)
  }

  const parameters = new URLSearchParams({
    response_type: 'code',
    client_id: config.clientId,
    redirect_uri: config.redirectUri,
    scope: config.scope,
    state,
    code_challenge: challenge,

    // S256 only. The spec also allows `plain`, which transmits the verifier itself and provides no
    // protection whatsoever against an intercepted code — the realm requires S256 too, so a
    // mismatch here fails loudly rather than degrading quietly.
    code_challenge_method: 'S256',
  })

  globalThis.location.assign(`${endpoints(config).authorize}?${parameters.toString()}`)
}

/** Thrown when a redirect cannot be turned into tokens. */
export class SignInError extends Error {}

/**
 * Completes the flow from the redirect URL.
 *
 * Returns `null` when the URL carries no authorization code, which is the ordinary case for every
 * navigation that is not the redirect back from Keycloak.
 */
export async function completeSignIn(config: OidcConfig, url: string): Promise<TokenSet | null> {
  const parameters = new URL(url).searchParams

  const error = parameters.get('error')
  if (error) {
    clearFlowState()
    throw new SignInError(`Authorization failed: ${error}`)
  }

  const code = parameters.get('code')
  if (!code) {
    return null
  }

  const expectedState = sessionStorage.getItem(STATE_KEY)
  const actualState = parameters.get('state')

  // The CSRF check. Without it, an attacker can hand a victim a link carrying the attacker's own
  // authorization code and have the victim's browser sign in as the attacker — after which
  // everything the victim types goes into the attacker's account.
  if (!expectedState || expectedState !== actualState) {
    clearFlowState()
    throw new SignInError('The sign-in response did not match the request that started it.')
  }

  const verifier = sessionStorage.getItem(VERIFIER_KEY)
  if (!verifier) {
    clearFlowState()
    throw new SignInError('The sign-in verifier is missing; start the sign-in again.')
  }

  const tokens = await requestTokens(config, {
    grant_type: 'authorization_code',
    client_id: config.clientId,
    code,
    redirect_uri: config.redirectUri,
    code_verifier: verifier,
  })

  clearFlowState()
  return tokens
}

/** Exchanges a refresh token for a new token set. */
export async function refreshTokens(config: OidcConfig, refreshToken: string): Promise<TokenSet> {
  return requestTokens(config, {
    grant_type: 'refresh_token',
    client_id: config.clientId,
    refresh_token: refreshToken,
  })
}

/** The path the employee was heading for before signing in, if any. */
export function takeReturnTo(): string | null {
  const value = sessionStorage.getItem(RETURN_TO_KEY)
  sessionStorage.removeItem(RETURN_TO_KEY)
  return value
}

/**
 * Ends the session at the authorization server.
 *
 * Dropping the local tokens is not signing out: the session at Keycloak stays open, so the next
 * sign-in completes silently and an employee who "signed out" on a shared machine did not. Going
 * through the end-session endpoint is also what triggers the back-channel logout callback that
 * writes this platform's revocation set.
 */
export function signOut(config: OidcConfig, idToken: string | null): void {
  const parameters = new URLSearchParams({
    post_logout_redirect_uri: config.postLogoutRedirectUri,
    client_id: config.clientId,
  })

  if (idToken) {
    parameters.set('id_token_hint', idToken)
  }

  globalThis.location.assign(`${endpoints(config).endSession}?${parameters.toString()}`)
}

function clearFlowState(): void {
  sessionStorage.removeItem(VERIFIER_KEY)
  sessionStorage.removeItem(STATE_KEY)
}

async function requestTokens(config: OidcConfig, body: Record<string, string>): Promise<TokenSet> {
  const response = await fetch(endpoints(config).token, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams(body).toString(),
  })

  if (!response.ok) {
    throw new SignInError(`The token endpoint refused the request (${String(response.status)}).`)
  }

  const payload = (await response.json()) as TokenResponse

  return {
    accessToken: payload.access_token,
    refreshToken: payload.refresh_token ?? null,
    idToken: payload.id_token ?? null,

    // Taken from the token's own `exp` where possible, falling back to `expires_in`. The two
    // disagree whenever the browser's clock is off, and `exp` is the value the API validates
    // against — refreshing on the browser's arithmetic instead would leave a client convinced its
    // token is valid while every request comes back 401.
    expiresAt: expiryOf(payload.access_token) ?? Date.now() + (payload.expires_in ?? 300) * 1000,
  }
}
