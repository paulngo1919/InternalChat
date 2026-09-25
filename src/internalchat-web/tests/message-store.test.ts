import { describe, expect, it } from 'vitest'

import {
  addTombstone,
  mergeMessages,
  TOMBSTONE_LIMIT,
} from '../src/features/messages/messageStore'
import { aMessage } from './helpers'

/**
 * 002 T033 — the client rules that make concurrent fan-out safe (hub contract 1.1.0).
 *
 * `realtime.fanout` now handles up to eight events at once, so events for one message can arrive in
 * any order: an edit before the send it edits, a send after the delete that removed it. The server
 * always delivers what is stored at the moment it handles an event, so the correct end state is
 * well defined — these rules are what make every client reach it regardless of arrival order.
 */

const at = (iso: string) => `2026-09-25T09:00:${iso}Z`

describe('latest-version rule', () => {
  it('keeps an edit when the original send arrives after it', () => {
    const edited = aMessage({ id: 'm1', seq: 1, body: 'fixed typo', editedAt: at('05') })
    const original = aMessage({ id: 'm1', seq: 1, body: 'fixd typo', editedAt: null })

    const merged = mergeMessages([edited], [original], 'c1', [])

    expect(merged).toHaveLength(1)
    expect(merged[0]?.body).toBe('fixed typo')
  })

  it('keeps the later of two edits whichever arrives last', () => {
    const second = aMessage({ id: 'm1', seq: 1, body: 'second', editedAt: at('09') })
    const first = aMessage({ id: 'm1', seq: 1, body: 'first', editedAt: at('03') })

    expect(mergeMessages([second], [first], 'c1', [])[0]?.body).toBe('second')
    expect(mergeMessages([first], [second], 'c1', [])[0]?.body).toBe('second')
  })

  it('applies a genuinely newer version', () => {
    const original = aMessage({ id: 'm1', seq: 1, body: 'before', editedAt: null })
    const edited = aMessage({ id: 'm1', seq: 1, body: 'after', editedAt: at('05') })

    expect(mergeMessages([original], [edited], 'c1', [])[0]?.body).toBe('after')
  })

  it('never lets a live copy undo a stored deletion', () => {
    const deleted = aMessage({ id: 'm1', seq: 1, body: null, deletedAt: at('07') })
    const late = aMessage({ id: 'm1', seq: 1, body: 'still here?', deletedAt: null })

    const merged = mergeMessages([deleted], [late], 'c1', [])

    expect(merged[0]?.deletedAt).not.toBeNull()
    expect(merged[0]?.body).toBeNull()
  })
})

describe('tombstone rule', () => {
  it('drops the content of a send that arrives after its own delete', () => {
    const late = aMessage({ id: 'm1', seq: 1, body: 'secret', deletedAt: null })

    const merged = mergeMessages([], [late], 'c1', ['m1'])

    expect(merged).toHaveLength(1)
    expect(merged[0]?.body).toBeNull()
    expect(merged[0]?.deletedAt).not.toBeNull()
  })

  it('drops the content of an edit that arrives after the delete', () => {
    const current = aMessage({ id: 'm1', seq: 1, body: 'text' })
    const edit = aMessage({ id: 'm1', seq: 1, body: 'edited text', editedAt: at('02') })

    const merged = mergeMessages([current], [edit], 'c1', ['m1'])

    expect(merged[0]?.body).toBeNull()
  })

  it('turns an already-shown message into a tombstone when its delete arrives', () => {
    const shown = aMessage({ id: 'm1', seq: 1, body: 'visible' })

    expect(mergeMessages([shown], [], 'c1', ['m1'])[0]?.body).toBeNull()
  })

  it('is bounded, forgetting the oldest ids first', () => {
    let tombstones: readonly string[] = []

    for (let i = 0; i < TOMBSTONE_LIMIT + 10; i++) {
      tombstones = addTombstone(tombstones, `m${String(i)}`)
    }

    expect(tombstones).toHaveLength(TOMBSTONE_LIMIT)
    expect(tombstones).not.toContain('m0')
    expect(tombstones).toContain(`m${String(TOMBSTONE_LIMIT + 9)}`)
  })

  it('does not record the same id twice', () => {
    expect(addTombstone(addTombstone([], 'm1'), 'm1')).toEqual(['m1'])
  })
})

describe('merging', () => {
  it('orders by seq, never by arrival', () => {
    const merged = mergeMessages(
      [aMessage({ id: 'm3', seq: 3 })],
      [aMessage({ id: 'm1', seq: 1 }), aMessage({ id: 'm2', seq: 2 })],
      'c1',
      [],
    )

    expect(merged.map((m) => m.seq)).toEqual([1, 2, 3])
  })

  it('ignores messages for other conversations', () => {
    expect(mergeMessages([], [aMessage({ id: 'x', conversationId: 'other' })], 'c1', [])).toEqual([])
  })

  it('returns the same array when nothing changed, so React can skip the render', () => {
    const current = [aMessage({ id: 'm1', seq: 1 })]

    expect(mergeMessages(current, [aMessage({ id: 'm1', seq: 1 })], 'c1', [])).toBe(current)
  })
})
