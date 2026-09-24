/**
 * T120 — mention autocomplete (FR-015, US3 scenario 5).
 *
 * Candidates are the conversation's own active members, not the whole directory. FR-015 restricts
 * mention resolution to active members at send time, so offering anyone else would let someone pick
 * a name the server discards silently — a confusing UI promise the API never keeps.
 */

import { useMemo } from 'react'

import { filterMentionCandidates, type MentionCandidate } from './mentionQuery'

export type { MentionCandidate } from './mentionQuery'

interface MentionAutocompleteProps {
  readonly query: string
  readonly candidates: readonly MentionCandidate[]
  readonly onSelect: (candidate: MentionCandidate) => void
}

/** The mention suggestion popup. Renders nothing when there is no match to offer. */
export function MentionAutocomplete({ query, candidates, onSelect }: MentionAutocompleteProps) {
  const matches = useMemo(() => filterMentionCandidates(query, candidates), [query, candidates])

  if (matches.length === 0) {
    return null
  }

  return (
    <ul role="listbox" aria-label="Mention a colleague" data-testid="mention-autocomplete">
      {matches.map((candidate) => (
        <li key={candidate.id}>
          <button
            type="button"
            role="option"
            aria-selected="false"
            data-testid="mention-option"
            // Mouse-down, not click: a click fires after the textarea's blur, by which point the
            // caret position this selection depends on may already have moved.
            onMouseDown={(event) => {
              event.preventDefault()
              onSelect(candidate)
            }}
          >
            {candidate.displayName}
          </button>
        </li>
      ))}
    </ul>
  )
}
