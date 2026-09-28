import type { ConversationResponse } from '../../lib/api/messages'

/** How a conversation is labelled when it has no name of its own. */
export function titleOf(conversation: ConversationResponse): string {
  if (conversation.name) {
    return conversation.name
  }

  // A direct conversation is named by who is in it, and the participant list arrives with the
  // members endpoint rather than the list projection. Until the list carries a counterparty name,
  // saying "Direct message" is better than rendering a raw UUID at somebody.
  return conversation.kind === 'direct' ? 'Direct message' : 'Untitled conversation'
}
