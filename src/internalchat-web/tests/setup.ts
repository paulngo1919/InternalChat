import '@testing-library/jest-dom/vitest'
import { afterEach, beforeEach } from 'vitest'

/**
 * Shared test setup.
 *
 * `sessionStorage` is cleared between tests because the PKCE verifier and state live there. A
 * verifier left behind by one test would let the next one complete a sign-in it never started —
 * which would make the CSRF assertions pass for the wrong reason, and those are the assertions
 * most worth trusting.
 */
beforeEach(() => {
  sessionStorage.clear()
})

afterEach(() => {
  sessionStorage.clear()
})
