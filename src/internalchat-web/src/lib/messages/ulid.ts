/**
 * ULID generation for `clientMessageKey` (FR-011, research.md D1).
 *
 * The key is generated once, in the browser, before the first send attempt — and reused unchanged
 * on every retry. That is the whole mechanism: the server's unique constraint on
 * `(conversation_id, client_message_key)` turns a retry into a no-op that returns the original
 * message. Generating a fresh key per attempt would turn one message into several, which is the
 * exact failure SC-022 forbids.
 *
 * Hand-rolled rather than taking the `ulid` package: it is a timestamp, some randomness, and
 * Crockford base32, and Principle VIII prefers one fewer dependency to re-verify at each bump. The
 * alphabet must match `ClientMessageKey` on the server, which rejects anything else.
 */

/** Crockford base32: digits and letters minus I, L, O, and U. */
const ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'

/** A ULID is 10 characters of timestamp followed by 16 of randomness. */
const TIMESTAMP_LENGTH = 10
const RANDOM_LENGTH = 16

/** Total length the server's parser requires. */
export const ULID_LENGTH = TIMESTAMP_LENGTH + RANDOM_LENGTH

/**
 * Generates a ULID.
 *
 * Lexicographically sortable by time, which is not why the server needs it — ordering comes from the
 * server sequence (FR-012) — but it does make a queue of pending sends readable in a debugger, and
 * it matches what the server's own generator produces so nothing downstream sees a shape no client
 * would send.
 */
export function newUlid(now: number = Date.now()): string {
  let timestamp = now
  const characters: string[] = new Array<string>(ULID_LENGTH)

  for (let i = TIMESTAMP_LENGTH - 1; i >= 0; i--) {
    characters[i] = ALPHABET[timestamp % 32] ?? '0'
    timestamp = Math.floor(timestamp / 32)
  }

  // crypto.getRandomValues, never Math.random. A predictable key is not a security hole here — the
  // server scopes it to a conversation the caller is already a member of — but a *colliding* one is
  // a correctness hole: it would be treated as a retry of somebody else's message and the send
  // would silently vanish.
  const random = new Uint8Array(RANDOM_LENGTH)
  crypto.getRandomValues(random)

  for (let i = 0; i < RANDOM_LENGTH; i++) {
    characters[TIMESTAMP_LENGTH + i] = ALPHABET[(random[i] ?? 0) % 32] ?? '0'
  }

  return characters.join('')
}

/** Whether a value is a well-formed ULID, using the same rule the server applies. */
export function isUlid(value: string): boolean {
  if (value.length !== ULID_LENGTH) {
    return false
  }

  for (const character of value) {
    if (!ALPHABET.includes(character)) {
      return false
    }
  }

  return true
}
