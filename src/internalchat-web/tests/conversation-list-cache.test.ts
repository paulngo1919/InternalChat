import { describe, expect, it } from 'vitest'

import { applyIncomingToConversationList } from '../src/features/conversations/conversationListCache'
import type { ConversationPage } from '../src/lib/api/messages'
import { aConversation, aMessage } from './helpers'

/**
 * 002 FR-006 / US1 scenario 3 — a conversation that is not open learns about a new message as it
 * arrives, not on the list's next refetch.
 *
 * Before this, the list refreshed only on a 30-second stale time or a membership event, so a
 * colleague's message into a conversation you were not looking at was invisible — no badge, no
 * preview — for up to half a minute, however fast the message itself had been delivered.
 *
 * Patched in place rather than by invalidating the query: invalidation would make every connected
 * client refetch its whole list for every message in every group it belongs to, which at 7,000
 * connections is a self-inflicted load test.
 */

const ME = 'me'
const COLLEAGUE = 'colleague'

function page(...items: ReturnType<typeof aConversation>[]): ConversationPage {
  return { items, nextCursor: null }
}

describe('applyIncomingToConversationList', () => {
  it('counts a colleague’s message in a conversation that is not open as unread, and previews it', () => {
    const before = page(aConversation({ id: 'c1', lastSeq: 4, unreadCount: 0 }))
    const message = aMessage({ conversationId: 'c1', seq: 5, authorId: COLLEAGUE, body: 'hello' })

    const { page: after } = applyIncomingToConversationList(before, [message], {
      openConversationId: null,
      currentEmployeeId: ME,
    })

    expect(after.items[0]?.unreadCount).toBe(1)
    expect(after.items[0]?.lastSeq).toBe(5)
    expect(after.items[0]?.lastMessage?.body).toBe('hello')
  })

  it('does not count a message in the conversation the reader has open', () => {
    const before = page(aConversation({ id: 'c1', lastSeq: 4, unreadCount: 0 }))

    const { page: after } = applyIncomingToConversationList(
      before,
      [aMessage({ conversationId: 'c1', seq: 5, authorId: COLLEAGUE })],
      { openConversationId: 'c1', currentEmployeeId: ME },
    )

    expect(after.items[0]?.unreadCount).toBe(0)
    expect(after.items[0]?.lastSeq).toBe(5)
  })

  it('does not count the reader’s own message, including from their other device', () => {
    const before = page(aConversation({ id: 'c1', lastSeq: 4, unreadCount: 0 }))

    const { page: after } = applyIncomingToConversationList(
      before,
      [aMessage({ conversationId: 'c1', seq: 5, authorId: ME })],
      { openConversationId: null, currentEmployeeId: ME },
    )

    expect(after.items[0]?.unreadCount).toBe(0)
  })

  it('is idempotent: a redelivered or already-listed sequence changes nothing', () => {
    // Delivery is at-least-once and Resync re-sends deliberately (contracts/signalr-hub.md).
    const before = page(aConversation({ id: 'c1', lastSeq: 5, unreadCount: 1 }))

    const { page: after } = applyIncomingToConversationList(
      before,
      [aMessage({ conversationId: 'c1', seq: 5, authorId: COLLEAGUE })],
      { openConversationId: null, currentEmployeeId: ME },
    )

    expect(after).toBe(before)
  })

  it('counts each new sequence once when a resync delivers several together', () => {
    const before = page(aConversation({ id: 'c1', lastSeq: 2, unreadCount: 0 }))
    const recovered = [3, 4, 5].map((seq) => aMessage({ conversationId: 'c1', seq, authorId: COLLEAGUE }))

    const { page: after } = applyIncomingToConversationList(before, [...recovered, ...recovered], {
      openConversationId: null,
      currentEmployeeId: ME,
    })

    expect(after.items[0]?.unreadCount).toBe(3)
    expect(after.items[0]?.lastSeq).toBe(5)
  })

  it('moves the conversation with the newest message to the top', () => {
    const before = page(
      aConversation({ id: 'c1', lastSeq: 9 }),
      aConversation({ id: 'c2', lastSeq: 3 }),
    )

    const { page: after } = applyIncomingToConversationList(
      before,
      [aMessage({ conversationId: 'c2', seq: 4, authorId: COLLEAGUE })],
      { openConversationId: null, currentEmployeeId: ME },
    )

    expect(after.items.map((c) => c.id)).toEqual(['c2', 'c1'])
  })

  it('reports a conversation it does not know, so the caller can refetch the list', () => {
    const before = page(aConversation({ id: 'c1' }))

    const result = applyIncomingToConversationList(
      before,
      [aMessage({ conversationId: 'unknown', seq: 1, authorId: COLLEAGUE })],
      { openConversationId: null, currentEmployeeId: ME },
    )

    expect(result.unknownConversation).toBe(true)
    expect(result.page).toBe(before)
  })
})
