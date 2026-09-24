/**
 * T120 — mention detection and filtering (FR-015, US3 scenario 5).
 *
 * Pure functions, tested without mounting a component or a real textarea — the caret-position logic
 * is the part most likely to be subtly wrong, and it does not need a DOM to prove.
 */

import { describe, expect, it } from 'vitest'

import {
  detectMentionQuery,
  filterMentionCandidates,
  type MentionCandidate,
} from '../src/features/messages/mentionQuery'
import { at } from './at'

describe('detectMentionQuery', () => {
  it('detects an @ just typed, with an empty query', () => {
    expect(detectMentionQuery('hello @', 7)).toBe('')
  })

  it('detects the partial name typed after @', () => {
    expect(detectMentionQuery('hello @an', 9)).toBe('an')
  })

  it('is null when there is no @ at all', () => {
    expect(detectMentionQuery('hello there', 11)).toBeNull()
  })

  it('is null once whitespace follows the @, even earlier in the same message', () => {
    // The caret has moved on past a finished (or abandoned) mention.
    expect(detectMentionQuery('hello @an said hi', 17)).toBeNull()
  })

  it('is null for an email-shaped token, where @ does not start a word', () => {
    expect(detectMentionQuery('reach me at an.nguyen@internalchat.local', 41)).toBeNull()
  })

  it('detects a mention at the very start of the message', () => {
    expect(detectMentionQuery('@an', 3)).toBe('an')
  })

  it('only looks at text up to the caret, not the whole message', () => {
    // The caret sits right after "@an" — text typed later in the message must not affect this.
    expect(detectMentionQuery('@an is typing @binh', 3)).toBe('an')
  })
})

describe('filterMentionCandidates', () => {
  const candidates: readonly MentionCandidate[] = [
    { id: '1', displayName: 'An Nguyen' },
    { id: '2', displayName: 'Binh Tran' },
    { id: '3', displayName: 'Chi Le' },
  ]

  it('returns every candidate for an empty query', () => {
    expect(filterMentionCandidates('', candidates)).toEqual(candidates)
  })

  it('matches case-insensitively, anywhere in the name', () => {
    expect(filterMentionCandidates('tran', candidates)).toEqual([at(candidates, 1)])
  })

  it('returns nothing when no name matches', () => {
    expect(filterMentionCandidates('zzz', candidates)).toEqual([])
  })

  it('caps the result at the suggestion limit', () => {
    const many: MentionCandidate[] = Array.from({ length: 20 }, (_, i) => ({
      id: String(i),
      displayName: `Person ${String(i)}`,
    }))

    expect(filterMentionCandidates('person', many).length).toBeLessThanOrEqual(6)
  })
})
