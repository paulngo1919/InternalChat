/**
 * T074 — protected routes.
 *
 * The session lives here and nowhere else, so no component has to reason about whether a token is
 * still valid. `RequireAuth` renders nothing until an employee is signed in, which is what makes
 * "protected" a property of the tree rather than a check each screen has to remember.
 */

import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'

import { AuthContext, useAuth, type AuthState } from './authContext'
import {
  beginSignIn,
  completeSignIn,
  signOut as endSession,
  takeReturnTo,
  type OidcConfig,
  type TokenSet,
} from './oidcClient'
import { TokenRefresh } from './TokenRefresh'

interface AuthProviderProps {
  readonly config: OidcConfig
  readonly children: ReactNode
}

/** Owns the session for the component tree. */
export function AuthProvider({ config, children }: AuthProviderProps) {
  const [status, setStatus] = useState<AuthState['status']>('starting')
  const [error, setError] = useState<string | null>(null)

  // Lazy state initialiser, so the refresher is constructed once and survives re-renders. Building
  // it during render would cancel the scheduled refresh on every render and quietly stop renewing.
  const [refresher] = useState(
    () =>
      new TokenRefresh(config, {
        onTokens: () => {
          setStatus('signed-in')
          setError(null)
        },
        onSessionLost: (reason) => {
          setStatus('signed-out')
          setError(reason)
        },
      }),
  )

  useEffect(() => {
    let cancelled = false

    async function resume(): Promise<void> {
      try {
        const tokens: TokenSet | null = await completeSignIn(config, globalThis.location.href)

        if (cancelled) {
          return
        }

        if (!tokens) {
          setStatus('signed-out')
          return
        }

        refresher.start(tokens)

        // The authorization code is single-use and already redeemed, but leaving it in the address
        // bar means a refresh replays a dead code and shows an error on a working session. Removing
        // it also keeps it out of the browser history.
        const returnTo = takeReturnTo() ?? '/'
        globalThis.history.replaceState({}, '', returnTo)
      } catch (cause) {
        if (!cancelled) {
          setStatus('error')
          setError(cause instanceof Error ? cause.message : 'Sign-in failed.')
        }
      }
    }

    void resume()

    return () => {
      cancelled = true
      refresher.stop()
    }
  }, [config, refresher])

  const value = useMemo<AuthState>(
    () => ({
      status,
      error,
      getAccessToken: () => refresher.getValidAccessToken(),
      signIn: () => {
        void beginSignIn(config, globalThis.location.pathname)
      },
      signOut: () => {
        const idToken = refresher.idToken
        refresher.stop()

        // Through the end-session endpoint, not by dropping tokens locally. Clearing them here
        // alone would leave the Keycloak session open, so the next sign-in would complete silently
        // and an employee who signed out on a shared machine did not.
        endSession(config, idToken)
      },
    }),
    [config, status, error, refresher],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

interface RequireAuthProps {
  readonly children: ReactNode
  /** Shown while the session is being established. */
  readonly fallback?: ReactNode
}

/**
 * Renders its children only for a signed-in employee.
 *
 * The redirect happens in an effect rather than during render, because navigating away mid-render
 * is a side effect in a place React is entitled to run twice.
 */
export function RequireAuth({ children, fallback }: RequireAuthProps) {
  const { status, signIn, error } = useAuth()

  const start = useCallback(() => {
    signIn()
  }, [signIn])

  useEffect(() => {
    if (status === 'signed-out') {
      start()
    }
  }, [status, start])

  if (status === 'signed-in') {
    return <>{children}</>
  }

  if (status === 'error') {
    return (
      <div role="alert">
        <h1>Sign-in failed</h1>
        <p>{error}</p>
        <button type="button" onClick={start}>
          Try again
        </button>
      </div>
    )
  }

  return <>{fallback ?? <p>Signing in…</p>}</>
}
