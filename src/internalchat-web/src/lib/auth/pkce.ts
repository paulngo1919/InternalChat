/**
 * PKCE (RFC 7636) primitives for the authorization code flow.
 *
 * Hand-rolled rather than taken from `oidc-client-ts`. The whole of PKCE is a random string, a
 * SHA-256, and base64url — about forty lines — and Constitution Principle VIII prefers one fewer
 * dependency whose licence has to be re-verified at every version bump. It also keeps the security
 * boundary readable: everything the browser does to protect the authorization code is on this page.
 *
 * Uses Web Crypto, which is available only in a secure context (HTTPS, or localhost). That is not a
 * limitation worth working around — a non-secure context cannot protect a token anyway.
 */

/** Characters used for the verifier. RFC 7636 §4.1 permits exactly these. */
const VERIFIER_ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~'

/**
 * RFC 7636 permits 43–128 characters. 64 is comfortably inside that and gives 384 bits of entropy
 * from the alphabet above — far beyond guessing, and short enough to sit in session storage without
 * thought.
 */
const VERIFIER_LENGTH = 64

/** A PKCE verifier and the challenge derived from it. */
export interface PkcePair {
  /** Held by the client until the code is redeemed. Never sent to the authorization endpoint. */
  readonly verifier: string
  /** SHA-256 of the verifier, base64url-encoded. Sent with the authorization request. */
  readonly challenge: string
}

/**
 * Generates cryptographically random bytes.
 *
 * `crypto.getRandomValues`, never `Math.random`. `Math.random` is seeded predictably in several
 * engines and is not a source of secrets — a guessable verifier defeats the entire point of PKCE,
 * and nothing about the resulting string would look wrong.
 */
function randomBytes(count: number): Uint8Array {
  const bytes = new Uint8Array(count)
  crypto.getRandomValues(bytes)
  return bytes
}

/**
 * base64url without padding, per RFC 7636 §A.
 *
 * Standard base64 would be rejected: `+` and `/` are not URL-safe, and `=` padding is explicitly
 * disallowed. Getting this wrong produces an authorization server error that reads as a
 * configuration problem rather than an encoding one.
 */
export function base64UrlEncode(bytes: Uint8Array): string {
  let binary = ''
  for (const byte of bytes) {
    binary += String.fromCharCode(byte)
  }

  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

/** Generates a random string from the RFC 7636 alphabet. */
export function randomString(length: number = VERIFIER_LENGTH): string {
  const bytes = randomBytes(length)

  let result = ''
  for (const byte of bytes) {
    // The index is always in range by construction, but `noUncheckedIndexedAccess` cannot know
    // that; the fallback is unreachable rather than defensive.
    result += VERIFIER_ALPHABET[byte % VERIFIER_ALPHABET.length] ?? 'A'
  }

  return result
}

/** Creates a verifier and its S256 challenge. */
export async function createPkcePair(): Promise<PkcePair> {
  const verifier = randomString()

  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))

  return { verifier, challenge: base64UrlEncode(new Uint8Array(digest)) }
}

/**
 * Decodes a JWT payload without verifying it.
 *
 * The client reads `exp` to decide when to refresh, and nothing else it does depends on the token
 * being genuine — the API validates every token it receives. Verifying here would mean shipping the
 * realm's public key to the browser and re-implementing signature checking to protect against an
 * attacker who, by definition, already controls the browser.
 *
 * Returns `null` for anything unparseable. A malformed token is treated as expired, which triggers
 * a refresh and then a sign-in — the safe direction.
 */
export function decodeJwtPayload(token: string): Record<string, unknown> | null {
  const segments = token.split('.')
  const encodedPayload = segments[1]

  if (segments.length !== 3 || encodedPayload === undefined) {
    return null
  }

  try {
    const payload = encodedPayload.replace(/-/g, '+').replace(/_/g, '/')
    const padded = payload.padEnd(payload.length + ((4 - (payload.length % 4)) % 4), '=')

    const decoded = decodeURIComponent(
      atob(padded)
        .split('')
        .map((character) => `%${`00${character.charCodeAt(0).toString(16)}`.slice(-2)}`)
        .join(''),
    )

    return JSON.parse(decoded) as Record<string, unknown>
  } catch {
    return null
  }
}

/** Reads `exp` as a millisecond timestamp, or `null` when the token declares none. */
export function expiryOf(token: string): number | null {
  const payload = decodeJwtPayload(token)
  const exp = payload?.exp

  return typeof exp === 'number' ? exp * 1000 : null
}
