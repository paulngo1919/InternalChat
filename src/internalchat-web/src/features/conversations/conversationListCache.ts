/**
 * 002 FR-006 — keeps the cached conversation list current as messages arrive.
 *
 * Without this, a message into a conversation the reader was not looking at reached the browser in
 * milliseconds and then showed nowhere: the list refreshed on its 30-second stale time, so the
 * unread badge and preview lagged the message by up to half a minute. Patching the cached page in
 * place is also what keeps the server out of it — invalidating would have every connected client
 * refetch its list for every message in every group it is in.
 */

import type { ConversationPage, ConversationResponse, MessageResponse } from '../../lib/api/messages'

export interface ConversationListContext {
  /** The conversation on screen. Its messages are being read as they arrive, so they are not unread. */
  readonly openConversationId: string | null
  /** The reader. Their own messages — from this device or another — are never unread to them. */
  readonly currentEmployeeId: string
}

export interface ConversationListPatch {
  /** The patched page, or the same object when nothing changed (so React skips a render). */
  readonly page: ConversationPage
  /**
   * A message arrived for a conversation not in the page — one the reader was just added to, or one
   * beyond the first page. The caller refetches; this function cannot invent the list row.
   */
  readonly unknownConversation: boolean
}

/**
 * Applies incoming messages to a cached list page.
 *
 * Idempotent by `seq`: a message at or below a conversation's `lastSeq` changes nothing, which is
 * what makes at-least-once delivery and `Resync`'s deliberate re-sends safe to feed through here.
 */
export function applyIncomingToConversationList(
  page: ConversationPage,
  messages: readonly MessageResponse[],
  context: ConversationListContext,
): ConversationListPatch {
  const byId = new Map(page.items.map((item) => [item.id, item]))
  const touched = new Set<string>()
  let unknownConversation = false

  const ordered = [...messages].sort((a, b) => a.seq - b.seq)

  for (const message of ordered) {
    const current = byId.get(message.conversationId)

    if (current === undefined) {
      unknownConversation = true
      continue
    }

    if (message.seq <= current.lastSeq) {
      continue
    }

    const unread =
      message.conversationId !== context.openConversationId &&
      message.authorId !== context.currentEmployeeId &&
      message.deletedAt === null

    const next: ConversationResponse = {
      ...current,
      lastSeq: message.seq,
      lastMessage: message,
      unreadCount: unread ? current.unreadCount + 1 : current.unreadCount,
    }

    byId.set(message.conversationId, next)
    touched.add(message.conversationId)
  }

  if (touched.size === 0) {
    return { page, unknownConversation }
  }

  // Most recently active first, as the server orders it: conversations that just received a
  // message move to the top, in the order they received it, and the rest keep their order.
  const moved = ordered
    .map((m) => m.conversationId)
    .filter((id) => touched.has(id))
    .reverse()
    .filter((id, index, all) => all.indexOf(id) === index)

  const items = [
    ...moved.map((id) => byId.get(id)).filter((item): item is ConversationResponse => item !== undefined),
    ...page.items.filter((item) => !touched.has(item.id)).map((item) => byId.get(item.id) ?? item),
  ]

  return { page: { ...page, items }, unknownConversation }
}
