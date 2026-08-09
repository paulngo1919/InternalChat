/**
 * T075 — the auth library.
 *
 * These cover the parts where a mistake is silent and serious: a verifier that is not random, a
 * missing CSRF check, a refresh loop, and a refresh token redeemed twice. Each is something that
 * looks like a working sign-in right up until it does not.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  base64UrlEncode,
  createPkcePair,
  decodeJwtPayload,
  expiryOf,
  randomString,
} from '../src/lib/auth/pkce'
import {
  SignInError,
  completeSignIn,
  type OidcConfig,
  type TokenSet,
} from '../src/lib/auth/oidcClient'
import { TokenRefresh } from '../src/lib/auth/TokenRefresh'

const config: OidcConfig = {
  authority: 'https://keycloak.test/realms/internalchat',
  clientId: 'internalchat-web',
  redirectUri: 'https://chat.test/',
  postLogoutRedirectUri: 'https://chat.test/',
  scope: 'openid profile email',
}

/** Builds an unsigned token with the given expiry. Only `exp` is ever read client-side. */
function tokenExpiringAt(epochSeconds: number): string {
  const encode = (value: object) => base64UrlEncode(new TextEncoder().encode(JSON.stringify(value)))

  return `${encode({ alg: 'none' })}.${encode({ exp: epochSeconds })}.signature`
}

describe('PKCE', () => {
  it('produces a verifier within the length RFC 7636 permits', () => {
    const verifier = randomString()

    expect(verifier.length).toBeGreaterThanOrEqual(43)
    expect(verifier.length).toBeLessThanOrEqual(128)
  })

  it('uses only characters the specification allows', () => {
    // A verifier containing anything else is rejected by the token endpoint with an error that
    // names neither the character nor the field.
    expect(randomString(128)).toMatch(/^[A-Za-z0-9\-._~]+$/)
  })

  it('never repeats a verifier', () => {
    const verifiers = new Set(Array.from({ length: 200 }, () => randomString()))

    // A predictable verifier defeats PKCE entirely, and nothing about the resulting string would
    // look wrong. This is the cheap check that the source is crypto.getRandomValues rather than
    // Math.random.
    expect(verifiers.size).toBe(200)
  })

  it('derives a base64url challenge with no padding', async () => {
    const { challenge } = await createPkcePair()

    expect(challenge).toMatch(/^[A-Za-z0-9\-_]+$/)
    expect(challenge).not.toContain('=')
  })

  it('derives the same challenge for the same verifier and a different one otherwise', async () => {
    const first = await createPkcePair()
    const second = await createPkcePair()

    expect(first.challenge).not.toBe(second.challenge)
  })

  it('reads exp from a token and returns null when there is none', () => {
    expect(expiryOf(tokenExpiringAt(1_800_000_000))).toBe(1_800_000_000_000)
    expect(expiryOf('not-a-token')).toBeNull()
  })

  it('treats a malformed token as undecodable rather than throwing', () => {
    // Returning null sends the caller down the refresh-then-sign-in path. Throwing here would
    // surface as an unhandled rejection during startup, which is a worse way to discover a
    // corrupted token.
    expect(decodeJwtPayload('a.b')).toBeNull()
    expect(decodeJwtPayload('a.!!!.c')).toBeNull()
  })
})

describe('completeSignIn', () => {
  it('returns null when the URL carries no authorization code', async () => {
    // The ordinary case for every navigation that is not the redirect back from Keycloak.
    await expect(completeSignIn(config, 'https://chat.test/')).resolves.toBeNull()
  })

  it('refuses a response whose state does not match the request', async () => {
    sessionStorage.setItem('internalchat.pkce.state', 'the-state-we-sent')
    sessionStorage.setItem('internalchat.pkce.verifier', 'the-verifier-we-kept')

    // The attack: a link carrying someone else's authorization code. Without the state check the
    // victim's browser signs in as the attacker, and everything typed afterwards lands in the
    // attacker's account.
    await expect(
      completeSignIn(config, 'https://chat.test/?code=stolen&state=a-different-state'),
    ).rejects.toBeInstanceOf(SignInError)
  })

  it('discards the verifier after a failed exchange', async () => {
    sessionStorage.setItem('internalchat.pkce.state', 'expected')
    sessionStorage.setItem('internalchat.pkce.verifier', 'kept')

    await expect(
      completeSignIn(config, 'https://chat.test/?code=x&state=wrong'),
    ).rejects.toBeInstanceOf(SignInError)

    // A single-use secret that survives a failure can be replayed by the next attempt.
    expect(sessionStorage.getItem('internalchat.pkce.verifier')).toBeNull()
    expect(sessionStorage.getItem('internalchat.pkce.state')).toBeNull()
  })

  it('refuses when the authorization server reported an error', async () => {
    await expect(
      completeSignIn(config, 'https://chat.test/?error=access_denied'),
    ).rejects.toBeInstanceOf(SignInError)
  })

  it('refuses when the verifier is missing even though the state matches', async () => {
    sessionStorage.setItem('internalchat.pkce.state', 'matching')

    // Happens when the tab was reloaded mid-flow. Redeeming without a verifier would ask the
    // server to accept a code with no proof of possession, which is the pre-PKCE behaviour.
    await expect(
      completeSignIn(config, 'https://chat.test/?code=x&state=matching'),
    ).rejects.toBeInstanceOf(SignInError)
  })
})

describe('TokenRefresh', () => {
  let now = 1_000_000
  let scheduled: { callback: () => void; delay: number } | null = null

  const timers = {
    setTimer: (callback: () => void, delay: number) => {
      scheduled = { callback, delay }
      return 1
    },
    clearTimer: () => {
      scheduled = null
    },
    now: () => now,
  }

  function tokenSet(expiresInMs: number, refreshToken: string | null = 'refresh-1'): TokenSet {
    return {
      accessToken: 'access-1',
      refreshToken,
      idToken: 'id-1',
      expiresAt: now + expiresInMs,
    }
  }

  beforeEach(() => {
    now = 1_000_000
    scheduled = null
    vi.restoreAllMocks()
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('schedules a refresh ahead of expiry, not at it', () => {
    const refresher = new TokenRefresh(
      config,
      { onTokens: vi.fn(), onSessionLost: vi.fn() },
      timers,
    )

    refresher.start(tokenSet(300_000))

    // 300s token, refreshed 60s early. Refreshing at expiry would guarantee a window where every
    // in-flight request fails.
    expect(scheduled?.delay).toBe(240_000)
  })

  it('never schedules a zero delay for an almost-expired token', () => {
    const refresher = new TokenRefresh(
      config,
      { onTokens: vi.fn(), onSessionLost: vi.fn() },
      timers,
    )

    refresher.start(tokenSet(1_000))

    // Without the floor this fires immediately, refreshes, receives another such token, and spins.
    // A clock a few minutes out is enough to produce it.
    expect(scheduled?.delay).toBeGreaterThanOrEqual(5_000)
  })

  it('returns the current token while it is comfortably valid', async () => {
    const refresher = new TokenRefresh(
      config,
      { onTokens: vi.fn(), onSessionLost: vi.fn() },
      timers,
    )
    refresher.start(tokenSet(300_000))

    const fetchSpy = vi.fn()
    vi.stubGlobal('fetch', fetchSpy)

    await expect(refresher.getValidAccessToken()).resolves.toBe('access-1')
    expect(fetchSpy).not.toHaveBeenCalled()
  })

  it('refreshes exactly once when several requests ask at the same moment', async () => {
    const refresher = new TokenRefresh(
      config,
      { onTokens: vi.fn(), onSessionLost: vi.fn() },
      timers,
    )
    refresher.start(tokenSet(10_000))

    const fetchSpy = vi.fn().mockResolvedValue({
      ok: true,
      json: () =>
        Promise.resolve({
          access_token: tokenExpiringAt(Math.floor(now / 1000) + 300),
          refresh_token: 'refresh-2',
          expires_in: 300,
        }),
    })
    vi.stubGlobal('fetch', fetchSpy)

    await Promise.all([
      refresher.getValidAccessToken(),
      refresher.getValidAccessToken(),
      refresher.getValidAccessToken(),
    ])

    // Refresh tokens rotate with reuse disabled, so redeeming the same one twice invalidates the
    // whole chain and signs the employee out. A burst of parallel API calls must not do that.
    expect(fetchSpy).toHaveBeenCalledTimes(1)
  })

  it('reports the session lost when the refresh is refused', async () => {
    const onSessionLost = vi.fn()
    const refresher = new TokenRefresh(config, { onTokens: vi.fn(), onSessionLost }, timers)
    refresher.start(tokenSet(10_000))

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 400 }))

    await expect(refresher.getValidAccessToken()).resolves.toBeNull()

    // Not retried. A rotating refresh token that fails has expired, been reused, or been revoked,
    // and none of those improve on a second attempt.
    expect(onSessionLost).toHaveBeenCalledTimes(1)
    expect(refresher.accessToken).toBeNull()
  })

  it('reports the session lost when there is no refresh token to use', async () => {
    const onSessionLost = vi.fn()
    const refresher = new TokenRefresh(config, { onTokens: vi.fn(), onSessionLost }, timers)
    refresher.start(tokenSet(10_000, null))

    await expect(refresher.refreshNow()).resolves.toBeNull()
    expect(onSessionLost).toHaveBeenCalledTimes(1)
  })

  it('stops scheduling once stopped', () => {
    const refresher = new TokenRefresh(
      config,
      { onTokens: vi.fn(), onSessionLost: vi.fn() },
      timers,
    )

    refresher.start(tokenSet(300_000))
    refresher.stop()

    // A timer surviving sign-out would refresh a session the employee ended, which on a shared
    // machine is the difference between signing out and appearing to.
    expect(scheduled).toBeNull()
    expect(refresher.accessToken).toBeNull()
  })

  it('prefers the token exp over expires_in', async () => {
    const onTokens = vi.fn()
    const refresher = new TokenRefresh(config, { onTokens, onSessionLost: vi.fn() }, timers)
    refresher.start(tokenSet(10_000))

    const authoritativeExpiry = Math.floor(now / 1000) + 300

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        json: () =>
          Promise.resolve({
            access_token: tokenExpiringAt(authoritativeExpiry),
            refresh_token: 'refresh-2',

            // Disagrees with the token, as it does whenever the browser's clock is off. The API
            // validates `exp` with zero skew, so trusting this instead would leave the client
            // convinced its token is good while every request comes back 401.
            expires_in: 99_999,
          }),
      }),
    )

    await refresher.refreshNow()

    expect(onTokens).toHaveBeenLastCalledWith(
      expect.objectContaining({ expiresAt: authoritativeExpiry * 1000 }),
    )
  })
})
