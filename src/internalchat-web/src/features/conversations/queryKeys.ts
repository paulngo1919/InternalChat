/**
 * TanStack Query cache keys.
 *
 * In their own module so the component files export components and nothing else — React Fast Refresh
 * silently stops working for a module that mixes the two, and the symptom is a dev server that
 * quietly needs a full reload to show a change.
 */

/** The signed-in employee's conversation list. Invalidated when a real-time event changes it. */
export const conversationsQueryKey = ['conversations'] as const

/** One conversation's history. Keyed by id so switching conversations does not refetch both. */
export const historyQueryKey = (conversationId: string) =>
  ['conversations', conversationId, 'messages'] as const

/** One conversation's active member list (US3). Invalidated by an add or a remove. */
export const membersQueryKey = (conversationId: string) =>
  ['conversations', conversationId, 'members'] as const
