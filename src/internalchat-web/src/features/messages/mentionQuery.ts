/**
 * Pure mention-detection helpers for the composer (T120, FR-015, US3 scenario 5).
 *
 * In their own module, split out of `MentionAutocomplete.tsx`, for the same reason
 * `conversations/queryKeys.ts` is split from its components — React Fast Refresh silently stops
 * working for a module that exports both components and plain functions.
 */

/** The minimum a candidate needs to be offered and inserted. */
export interface MentionCandidate {
  readonly id: string
  readonly displayName: string
}

/** How many suggestions to show at once. Long enough to be useful, short enough to stay a popup. */
export const MAX_MENTION_SUGGESTIONS = 6

/**
 * Finds the `@name` fragment the caret is currently inside, or `null` when it is not inside one.
 */
export function detectMentionQuery(text: string, caretIndex: number): string | null {
  const upToCaret = text.slice(0, Math.max(0, caretIndex))
  const atIndex = upToCaret.lastIndexOf('@')

  if (atIndex === -1) {
    return null
  }

  const between = upToCaret.slice(atIndex + 1)

  // Whitespace between the `@` and the caret means that mention was already finished (or abandoned)
  // and the caret has moved on to ordinary text.
  if (/\s/.test(between)) {
    return null
  }

  const before = atIndex === 0 ? '' : upToCaret[atIndex - 1]

  // The `@` must start a word. Otherwise `name@company.com` would trigger a mention halfway through
  // an email address the person is typing.
  if (before && !/\s/.test(before)) {
    return null
  }

  return between
}

/** Narrows candidates to those matching the query, case-insensitively, capped for the popup. */
export function filterMentionCandidates(
  query: string,
  candidates: readonly MentionCandidate[],
): readonly MentionCandidate[] {
  const needle = query.trim().toLowerCase()

  const matches =
    needle.length === 0
      ? candidates
      : candidates.filter((candidate) => candidate.displayName.toLowerCase().includes(needle))

  return matches.slice(0, MAX_MENTION_SUGGESTIONS)
}
