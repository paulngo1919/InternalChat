/**
 * T074 — silent token refresh.
 *
 * Access tokens live five minutes (research.md D5), which is short on purpose: it is what bounds
 * the worst case for FR-003's five-minute revocation deadline. The cost of that choice is that a
 * client doing nothing about it would show an employee a sign-in prompt twelve times an hour.
 *
 * This refreshes ahead of expiry, from a rotating refresh token, without a redirect.
 */

import type { OidcConfig, TokenSet } from './oidcClient'
import { refreshTokens } from './oidcClient'

/**
 * How far ahead of expiry to refresh.
 *
 * Long enough to absorb a slow network and a modest clock difference between the browser and the
 * realm; short enough that a token is not replaced for most of its life. The API validates `exp`
 * with **zero** clock skew — deliberately, because a five-minute tolerance would double the
 * effective token life and break the revocation budget — so the margin has to live here instead.
 */
const REFRESH_MARGIN_MS = 60_000

/**
 * Shortest delay that will ever be scheduled.
 *
 * Without a floor, a token that arrives already inside the margin schedules at zero and the timer
 * fires immediately, refreshes, gets another such token, and spins — a refresh loop that looks like
 * a network storm and is trivial to create by getting a clock wrong.
 */
const MINIMUM_DELAY_MS = 5_000

/** Notified whenever the token set changes or is lost. */
export interface TokenRefreshListener {
  /** A new token set is in force. */
  onTokens(tokens: TokenSet): void
  /**
   * The session has ended and cannot be recovered silently.
   *
   * The caller must send the employee back through sign-in. It is not an error state to be retried:
   * a rotating refresh token that fails has either expired, been used twice, or been revoked, and
   * none of those get better by asking again.
   */
  onSessionLost(reason: string): void
}

/**
 * Keeps an access token fresh for as long as the refresh token allows.
 *
 * Deliberately not a React hook. The scheduler outlives any component, has to survive re-renders,
 * and is the thing an API client asks for a token — all of which are awkward inside a hook and
 * simple in a plain object. `AuthProvider` wraps it for the component tree.
 */
export class TokenRefresh {
  private readonly config: OidcConfig
  private readonly listener: TokenRefreshListener
  private readonly setTimer: (callback: () => void, delayMs: number) => number
  private readonly clearTimer: (handle: number) => void
  private readonly now: () => number

  private tokens: TokenSet | null = null
  private handle: number | null = null
  private inFlight: Promise<TokenSet> | null = null

  /**
   * @param timers Injected so tests can drive time rather than wait for it. A test that genuinely
   *   waited five minutes to prove a refresh happens would not be run.
   */
  constructor(
    config: OidcConfig,
    listener: TokenRefreshListener,
    timers?: {
      setTimer?: (callback: () => void, delayMs: number) => number
      clearTimer?: (handle: number) => void
      now?: () => number
    },
  ) {
    this.config = config
    this.listener = listener
    // `window.setTimeout`, not the bare global. The bare `setTimeout` returns a plain number under
    // DOM types and an opaque `Timeout` object once anything drags Node's types into the program —
    // so its return type differs between the app's compilation and the test one, and no single
    // spelling of a cast satisfies both. The DOM overload is unambiguous, and this code runs in a
    // browser, so naming it is also the more honest of the two.
    this.setTimer = timers?.setTimer ?? ((callback, delay) => window.setTimeout(callback, delay))
    this.clearTimer =
      timers?.clearTimer ??
      ((handle) => {
        window.clearTimeout(handle)
      })
    this.now = timers?.now ?? (() => Date.now())
  }

  /** The current access token, or `null` when there is no session. */
  get accessToken(): string | null {
    return this.tokens?.accessToken ?? null
  }

  /** The current id token, needed to end the session at the authorization server. */
  get idToken(): string | null {
    return this.tokens?.idToken ?? null
  }

  /** Adopts a token set and schedules the next refresh. */
  start(tokens: TokenSet): void {
    this.tokens = tokens
    this.listener.onTokens(tokens)
    this.schedule()
  }

  /** Stops refreshing and forgets the tokens. */
  stop(): void {
    if (this.handle !== null) {
      this.clearTimer(this.handle)
      this.handle = null
    }

    this.tokens = null
    this.inFlight = null
  }

  /**
   * Returns a token that is valid now, refreshing first if it is about to expire.
   *
   * Called by the API client before every request. The scheduled refresh handles the common case;
   * this covers the ones it cannot — a laptop resumed from sleep, where the timer that should have
   * fired an hour ago did not.
   */
  async getValidAccessToken(): Promise<string | null> {
    if (!this.tokens) {
      return null
    }

    if (this.tokens.expiresAt - this.now() > REFRESH_MARGIN_MS) {
      return this.tokens.accessToken
    }

    const refreshed = await this.refreshNow()
    return refreshed?.accessToken ?? null
  }

  /**
   * Refreshes immediately.
   *
   * Concurrent callers share one in-flight request. Refresh tokens rotate with reuse disabled
   * (deploy/keycloak/realm-export.json), so two simultaneous refreshes would redeem the same token
   * twice — and Keycloak's correct response to that is to invalidate the whole chain, signing the
   * employee out. The single-flight guard is what stops a burst of parallel requests doing it.
   */
  async refreshNow(): Promise<TokenSet | null> {
    if (this.inFlight) {
      return this.inFlight
    }

    const refreshToken = this.tokens?.refreshToken
    if (!refreshToken) {
      this.stop()
      this.listener.onSessionLost('No refresh token is available.')
      return null
    }

    this.inFlight = refreshTokens(this.config, refreshToken)

    try {
      const tokens = await this.inFlight
      this.tokens = tokens
      this.listener.onTokens(tokens)
      this.schedule()
      return tokens
    } catch {
      this.stop()
      this.listener.onSessionLost('The session could not be renewed.')
      return null
    } finally {
      this.inFlight = null
    }
  }

  private schedule(): void {
    if (this.handle !== null) {
      this.clearTimer(this.handle)
      this.handle = null
    }

    if (!this.tokens) {
      return
    }

    const delay = Math.max(this.tokens.expiresAt - this.now() - REFRESH_MARGIN_MS, MINIMUM_DELAY_MS)

    this.handle = this.setTimer(() => {
      void this.refreshNow()
    }, delay)
  }
}
