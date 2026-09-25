/**
 * 002 T041 — how a conversation's transcript absorbs events that arrive in any order.
 *
 * Hub contract 1.1.0: `realtime.fanout` handles up to eight events at once, so `MessageReceived`,
 * `MessageEdited`, and `MessageDeleted` for the same message are not guaranteed to arrive in the
 * order they happened. Two rules make the end state independent of arrival order:
 *
 * - **Latest version wins.** For one id, keep the copy with the greatest `editedAt`; a deleted copy
 *   beats any live one. A send that lands after its own edit cannot restore the old text.
 * - **Tombstones stick.** Once a delete for an id has been seen, any later copy of it is shown as
 *   deleted. A send that lands after its own delete cannot bring the content back.
 *
 * Pure functions over plain arrays, so the rules are testable without rendering anything.
 */

import type { MessageResponse } from '../../lib/api/messages'

/** How many deleted ids are remembered per conversation (contracts/signalr-hub-delta.md). */
export const TOMBSTONE_LIMIT = 500

/** Records a deleted id, forgetting the oldest once the bound is reached. */
export function addTombstone(tombstones: readonly string[], messageId: string): readonly string[] {
  if (tombstones.includes(messageId)) {
    return tombstones
  }

  const next = [...tombstones, messageId]
  return next.length > TOMBSTONE_LIMIT ? next.slice(next.length - TOMBSTONE_LIMIT) : next
}

/** The copy of one message to keep, by the latest-version rule. */
function newer(current: MessageResponse, incoming: MessageResponse): MessageResponse {
  if (current.deletedAt !== null) {
    return current
  }

  if (incoming.deletedAt !== null) {
    return incoming
  }

  const currentEdit = current.editedAt === null ? -Infinity : Date.parse(current.editedAt)
  const incomingEdit = incoming.editedAt === null ? -Infinity : Date.parse(incoming.editedAt)

  // Ties keep the current copy, so a redelivery of the same version changes nothing.
  return incomingEdit > currentEdit ? incoming : current
}

/** A message rendered as deleted. The id and `seq` survive so ordering and continuity hold. */
function asTombstone(message: MessageResponse): MessageResponse {
  if (message.deletedAt !== null && message.body === null) {
    return message
  }

  return {
    ...message,
    body: null,
    attachments: [],
    mentions: [],
    deletedAt: message.deletedAt ?? new Date().toISOString(),
  }
}

/**
 * Merges arriving copies into a transcript, applying both rules, ordered by `seq`.
 *
 * Returns `current` itself when nothing changed, so a redelivery does not cost a render.
 */
export function mergeMessages(
  current: readonly MessageResponse[],
  arriving: readonly MessageResponse[],
  conversationId: string,
  tombstones: readonly string[],
): readonly MessageResponse[] {
  const deleted = new Set(tombstones)
  const byId = new Map(current.map((m) => [m.id, m]))
  let changed = false

  for (const message of arriving) {
    if (message.conversationId !== conversationId) {
      continue
    }

    const existing = byId.get(message.id)
    const kept = existing === undefined ? message : newer(existing, message)

    if (kept !== existing) {
      byId.set(message.id, kept)
      changed = true
    }
  }

  for (const [id, message] of byId) {
    if (deleted.has(id)) {
      const tombstone = asTombstone(message)

      if (tombstone !== message) {
        byId.set(id, tombstone)
        changed = true
      }
    }
  }

  if (!changed) {
    return current
  }

  return [...byId.values()].sort((a, b) => a.seq - b.seq)
}
