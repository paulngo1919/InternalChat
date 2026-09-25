/**
 * T100 — the conversation list.
 *
 * TanStack Query owns the fetching, caching, and revalidation so this component owns none of it. The
 * alternative — `useEffect` plus `useState` — is where double-fetching, stale reads after a
 * reconnect, and lost race conditions come from, and none of them are visible in a screenshot.
 */

import { useQuery } from '@tanstack/react-query'

import type { ConversationResponse, MessagingClient } from '../../lib/api/messages'
import { conversationsQueryKey } from './queryKeys'

interface ConversationListProps {
  readonly client: MessagingClient
  readonly selectedId: string | null
  readonly onSelect: (conversationId: string) => void
}

/** How a conversation is labelled when it has no name of its own. */
function titleOf(conversation: ConversationResponse): string {
  if (conversation.name) {
    return conversation.name
  }

  // A direct conversation is named by who is in it, and the participant list arrives with the
  // members endpoint rather than the list projection. Until the list carries a counterparty name,
  // saying "Direct message" is better than rendering a raw UUID at somebody.
  return conversation.kind === 'direct' ? 'Direct message' : 'Untitled conversation'
}

/** The signed-in employee's conversations, most recently active first. */
export function ConversationList({ client, selectedId, onSelect }: ConversationListProps) {
  const { data, isPending, isError, error } = useQuery({
    queryKey: conversationsQueryKey,
    queryFn: () => client.listConversations(),

    // Long enough that switching conversations does not refetch the list, short enough that a
    // conversation created in another tab appears without a reload. Real-time events invalidate this
    // key directly, so the interval is a backstop rather than the mechanism.
    staleTime: 30_000,
  })

  if (isPending) {
    return <p>Loading conversations…</p>
  }

  if (isError) {
    return (
      <p role="alert">
        Could not load your conversations. {error instanceof Error ? error.message : ''}
      </p>
    )
  }

  if (data.items.length === 0) {
    return <p data-testid="no-conversations">You have no conversations yet.</p>
  }

  return (
    <nav aria-label="Conversations" className="conversation-list-container">
      <ul className="conversation-list">
        {data.items.map((conversation) => (
          <li key={conversation.id}>
            <button
              type="button"
              className={`conversation-item ${conversation.id === selectedId ? 'active' : ''}`}
              onClick={() => {
                onSelect(conversation.id)
              }}
              aria-current={conversation.id === selectedId ? 'true' : undefined}
              data-testid="conversation-item"
            >
              <div className="conversation-item-header">
                <span className="conversation-title">{titleOf(conversation)}</span>
                {conversation.unreadCount > 0 && (
                  <span className="unread-badge" aria-label={`${String(conversation.unreadCount)} unread`}>
                    {conversation.unreadCount}
                  </span>
                )}
              </div>

              {conversation.lastMessage?.body && (
                <span className="conversation-preview" data-testid="conversation-preview">
                  {conversation.lastMessage.body}
                </span>
              )}
            </button>
          </li>
        ))}
      </ul>
    </nav>
  )
}
