import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { readApiBaseUrl, readOidcConfig } from '../src/lib/auth/config'
import {
  SignInError,
  beginSignIn,
  completeSignIn,
  refreshTokens,
  signOut,
  takeReturnTo,
  type OidcConfig,
} from '../src/lib/auth/oidcClient'
import { base64UrlEncode } from '../src/lib/auth/pkce'
import { at } from './at'

/**
 * The redirect halves of the OIDC flow, and the configuration they are built from.
 *
 * `tests/auth.test.ts` covers the parts where a mistake is silent and serious — a non-random
 * verifier, a missing CSRF check, a refresh loop. This file covers the rest of the round trip: what
 * actually goes into the authorization URL, what comes back out of a successful exchange, and what
 * `signOut` does, which is the one place where doing the obvious thing (dropping the local tokens)
 * would be wrong in a way nobody would notice until a shared machine was involved.
 */

const config: OidcConfig = {
  authority: 'https://keycloak.test/realms/internalchat',
  clientId: 'internalchat-web',
  redirectUri: 'https://chat.test/',
  postLogoutRedirectUri: 'https://chat.test/',
  scope: 'openid profile email',
}

const originalFetch = globalThis.fetch
const originalLocation = Object.getOwnPropertyDescriptor(globalThis, 'location')
const origin = globalThis.location.origin

let assigned: string[] = []

/**
 * Replaces `location` wholesale, since jsdom refuses real navigation.
 *
 * The whole object rather than just `assign`: jsdom defines `assign` as non-writable and
 * non-configurable, so spying on it throws. `location` itself is a configurable accessor, which is
 * the seam that exists.
 */
function captureNavigation() {
  assigned = []

  Object.defineProperty(globalThis, 'location', {
    configurable: true,
    value: {
      origin,
      href: `${origin}/`,
      pathname: '/',
      assign: (url: string | URL) => assigned.push(String(url)),
    },
  })
}

/** A token endpoint that answers with the given body and status. */
function stubTokenEndpoint(body: unknown, status = 200) {
  const calls: { url: string; body: string }[] = []

  globalThis.fetch = vi.fn((url: string | URL | Request, init?: RequestInit) => {
    // Narrowed rather than stringified: `String()` on a Request or a stream body gives
    // '[object Object]', and every request these tests make carries a form-encoded string.
    // Named apart from `body`, which is the RESPONSE this stub answers with.
    const sent = init?.body

    calls.push({
      url: url instanceof Request ? url.url : String(url),
      body: typeof sent === 'string' ? sent : '',
    })

    return Promise.resolve({
      ok: status >= 200 && status < 300,
      status,
      json: () => Promise.resolve(body),
    } as Response)
  })

  return calls
}

/** An unsigned token carrying only `exp`, which is all this client ever reads. */
function tokenExpiringAt(epochSeconds: number): string {
  const encode = (value: object) => base64UrlEncode(new TextEncoder().encode(JSON.stringify(value)))

  return `${encode({ alg: 'none' })}.${encode({ exp: epochSeconds })}.signature`
}

beforeEach(() => {
  captureNavigation()
})

afterEach(() => {
  globalThis.fetch = originalFetch

  if (originalLocation) {
    Object.defineProperty(globalThis, 'location', originalLocation)
  }

  vi.restoreAllMocks()
})

describe('configuration', () => {
  it('derives the redirect URIs from the current origin rather than configuration', () => {
    const resolved = readOidcConfig({ VITE_OIDC_AUTHORITY: 'https://keycloak.test/realms/ic' })

    // A redirect URI that disagrees with where the app is actually served is rejected by Keycloak
    // with an error naming neither value. Deriving it means the two cannot disagree.
    expect(resolved.redirectUri).toBe(`${globalThis.location.origin}/`)
    expect(resolved.postLogoutRedirectUri).toBe(`${globalThis.location.origin}/`)
  })

  it('requests only the scopes it uses', () => {
    const resolved = readOidcConfig({ VITE_OIDC_AUTHORITY: 'https://keycloak.test/realms/ic' })

    // `openid` is mandatory; `profile` and `email` supply the name and address colleagues see.
    // A scope this client does not use is a permission granted for no reason.
    expect(resolved.scope).toBe('openid profile email')
  })

  it('defaults the client id but never the authority', () => {
    const resolved = readOidcConfig({ VITE_OIDC_AUTHORITY: 'https://keycloak.test/realms/ic' })

    expect(resolved.clientId).toBe('internalchat-web')

    // Fails at startup rather than at first sign-in. A missing authority that surfaces only when
    // someone clicks "sign in" is a deployment that looks healthy and serves nobody.
    expect(() => readOidcConfig({})).toThrow(/VITE_OIDC_AUTHORITY/)
    expect(() => readOidcConfig({ VITE_OIDC_AUTHORITY: '' })).toThrow(/deploy\/\.env\.example/)
  })

  it('honours an explicit client id', () => {
    const resolved = readOidcConfig({
      VITE_OIDC_AUTHORITY: 'https://keycloak.test/realms/ic',
      VITE_OIDC_CLIENT_ID: 'internalchat-staging',
    })

    expect(resolved.clientId).toBe('internalchat-staging')
  })

  it('defaults the API base to the path behind the reverse proxy', () => {
    expect(readApiBaseUrl({})).toBe('/api/v1')
    expect(readApiBaseUrl({ VITE_API_BASE_URL: 'https://api.test/v1' })).toBe('https://api.test/v1')
  })
})

describe('beginSignIn', () => {
  it('redirects to the authorization endpoint with an S256 challenge', async () => {
    await beginSignIn(config)

    const url = new URL(at(assigned, 0))

    expect(url.origin + url.pathname).toBe(
      'https://keycloak.test/realms/internalchat/protocol/openid-connect/auth',
    )
    expect(url.searchParams.get('response_type')).toBe('code')
    expect(url.searchParams.get('client_id')).toBe('internalchat-web')
    expect(url.searchParams.get('redirect_uri')).toBe('https://chat.test/')
    expect(url.searchParams.get('scope')).toBe('openid profile email')

    // S256 only. The spec also allows `plain`, which transmits the verifier itself and provides no
    // protection whatsoever against an intercepted code.
    expect(url.searchParams.get('code_challenge_method')).toBe('S256')
    expect(url.searchParams.get('code_challenge')).toMatch(/^[A-Za-z0-9\-_]{43}$/)
  })

  it('keeps the verifier and state in sessionStorage, not localStorage', async () => {
    await beginSignIn(config)

    const state = sessionStorage.getItem('internalchat.pkce.state')

    expect(sessionStorage.getItem('internalchat.pkce.verifier')).toBeTruthy()
    expect(state).toBe(new URL(at(assigned, 0)).searchParams.get('state'))

    // They belong to one tab's attempt and are worthless afterwards. localStorage would share them
    // across tabs and outlive the browser session — a longer life than a single-use secret should
    // have.
    expect(localStorage.getItem('internalchat.pkce.verifier')).toBeNull()
  })

  it('tolerates an authority with a trailing slash', async () => {
    await beginSignIn({ ...config, authority: 'https://keycloak.test/realms/internalchat/' })

    // Otherwise the URL contains a double slash, which Keycloak answers with a 404 that reads like
    // the realm does not exist.
    expect(at(assigned, 0)).toContain('/realms/internalchat/protocol/openid-connect/auth?')
    expect(at(assigned, 0)).not.toContain('//protocol')
  })

  it('remembers where the employee was heading, and only when there is one', async () => {
    await beginSignIn(config, '/conversations/c1?seq=42')

    expect(sessionStorage.getItem('internalchat.returnTo')).toBe('/conversations/c1?seq=42')

    // Taken once. Leaving it behind would send the next sign-in to a stale deep link.
    expect(takeReturnTo()).toBe('/conversations/c1?seq=42')
    expect(takeReturnTo()).toBeNull()

    sessionStorage.clear()
    await beginSignIn(config)

    expect(sessionStorage.getItem('internalchat.returnTo')).toBeNull()
  })
})

describe('completeSignIn', () => {
  it('exchanges the code and clears the single-use flow state', async () => {
    await beginSignIn(config, '/conversations/c1')

    const state = sessionStorage.getItem('internalchat.pkce.state')
    const verifier = sessionStorage.getItem('internalchat.pkce.verifier')

    const calls = stubTokenEndpoint({
      access_token: tokenExpiringAt(Math.floor(Date.now() / 1000) + 300),
      refresh_token: 'refresh',
      id_token: 'id',
      expires_in: 300,
    })

    const tokens = await completeSignIn(config, `https://chat.test/?code=abc&state=${String(state)}`)

    expect(tokens?.refreshToken).toBe('refresh')

    const body = new URLSearchParams(at(calls, 0).body)

    expect(at(calls, 0).url).toBe(
      'https://keycloak.test/realms/internalchat/protocol/openid-connect/token',
    )
    expect(body.get('grant_type')).toBe('authorization_code')
    expect(body.get('code')).toBe('abc')
    expect(body.get('code_verifier')).toBe(verifier)

    // Cleared, because the code is single-use and the verifier protects exactly one exchange.
    expect(sessionStorage.getItem('internalchat.pkce.verifier')).toBeNull()
    expect(sessionStorage.getItem('internalchat.pkce.state')).toBeNull()

    // But the return path survives the exchange — it is consumed by the provider afterwards.
    expect(takeReturnTo()).toBe('/conversations/c1')
  })

  it('reports a refusal from the token endpoint', async () => {
    await beginSignIn(config)

    const state = sessionStorage.getItem('internalchat.pkce.state')

    stubTokenEndpoint({}, 400)

    await expect(
      completeSignIn(config, `https://chat.test/?code=abc&state=${String(state)}`),
    ).rejects.toBeInstanceOf(SignInError)
  })
})

describe('refreshTokens', () => {
  it('redeems the refresh token against the token endpoint', async () => {
    const calls = stubTokenEndpoint({
      access_token: tokenExpiringAt(Math.floor(Date.now() / 1000) + 300),
      refresh_token: 'refresh-2',
      expires_in: 300,
    })

    const tokens = await refreshTokens(config, 'refresh-1')

    const body = new URLSearchParams(at(calls, 0).body)

    expect(body.get('grant_type')).toBe('refresh_token')
    expect(body.get('refresh_token')).toBe('refresh-1')

    // The rotated token. Keeping the old one would make the next refresh redeem a token Keycloak
    // has already invalidated, which ends the session mid-use.
    expect(tokens.refreshToken).toBe('refresh-2')
  })
})

describe('signOut', () => {
  it('goes through the end-session endpoint rather than dropping tokens locally', () => {
    signOut(config, 'the-id-token')

    const url = new URL(at(assigned, 0))

    // Clearing tokens locally leaves the session open at Keycloak, so the next sign-in completes
    // silently — and an employee who "signed out" on a shared machine did not. Going through the
    // endpoint is also what triggers the back-channel logout that writes the revocation set.
    expect(url.origin + url.pathname).toBe(
      'https://keycloak.test/realms/internalchat/protocol/openid-connect/logout',
    )
    expect(url.searchParams.get('id_token_hint')).toBe('the-id-token')
    expect(url.searchParams.get('post_logout_redirect_uri')).toBe('https://chat.test/')
    expect(url.searchParams.get('client_id')).toBe('internalchat-web')
  })

  it('still ends the session when no id token is held', () => {
    signOut(config, null)

    const url = new URL(at(assigned, 0))

    // Without the hint Keycloak may ask the employee to confirm, which is worse than a silent
    // logout but far better than not logging out at all.
    expect(url.searchParams.has('id_token_hint')).toBe(false)
    expect(url.pathname).toContain('/logout')
  })
})
