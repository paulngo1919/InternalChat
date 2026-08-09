import { createContext, useContext } from 'react'

/**
 * The session, as the rest of the app is allowed to see it.
 *
 * Kept apart from `AuthProvider.tsx` so that file exports components and nothing else — React Fast
 * Refresh silently stops working for a module that mixes components with other exports, and the
 * symptom is a dev server that quietly requires a full reload to reflect a change.
 */
export interface AuthState {
  readonly status: 'starting' | 'signed-out' | 'signed-in' | 'error'
  readonly error: string | null
  /**
   * Returns a token valid at the moment of the call, refreshing first if needed.
   *
   * A function rather than a value on purpose. Handing components the token string invites them to
   * cache it, and a cached five-minute token is a request that fails after five minutes for reasons
   * nobody can see.
   */
  readonly getAccessToken: () => Promise<string | null>
  readonly signIn: () => void
  readonly signOut: () => void
}

/** Internal. Provided by `AuthProvider`, read through `useAuth`. */
export const AuthContext = createContext<AuthState | null>(null)

/**
 * Reads the session.
 *
 * Throws outside the provider rather than returning a signed-out-looking default. A default would
 * make a mis-mounted component render the sign-in prompt forever, which looks like an auth bug and
 * is a tree-shape one.
 */
export function useAuth(): AuthState {
  const context = useContext(AuthContext)

  if (!context) {
    throw new Error('useAuth must be used inside <AuthProvider>.')
  }

  return context
}
