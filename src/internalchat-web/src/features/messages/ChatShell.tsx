/**
 * The messaging screen: conversation list, transcript, composer, and the live connection.
 *
 * This is the only place that owns a `ChatConnection`. One connection per tab, not per conversation:
 * the hub delivers for every conversation the employee belongs to, and opening one per open
 * conversation would multiply 7,000 concurrent connections by however many tabs people keep.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'

import { createMessagingClient, type MessageResponse } from '../../lib/api/messages'
import { ChatConnection } from '../../lib/realtime/chatConnection'
import { ConversationList } from '../conversations/ConversationList'
import { CreateGroupForm } from '../conversations/GroupSettings'
import { conversationsQueryKey } from '../conversations/queryKeys'
import { SearchPanel } from '../search/SearchPanel'
import { ConversationView } from './ConversationView'

interface ChatShellProps {
  /** Issues authorized requests. Comes from the API client so the token handling is shared. */
  readonly authorized: (path: string, init?: RequestInit) => Promise<Response>
  readonly getAccessToken: () => Promise<string | null>
  readonly currentEmployeeId: string
}

/** The messaging screen. */
export function ChatShell({ authorized, getAccessToken, currentEmployeeId }: ChatShellProps) {
  const [client] = useState(() => createMessagingClient(authorized))
  const queryClient = useQueryClient()

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [incoming, setIncoming] = useState<readonly MessageResponse[]>([])
  const [connected, setConnected] = useState(false)
  const [typing, setTyping] = useState<Readonly<Record<string, readonly string[]>>>({})

  // Read inside the connection effect below, which is built once and must still see the currently
  // open conversation — not the one that was open when the connection was constructed.
  const selectedIdRef = useRef<string | null>(null)
  useEffect(() => {
    selectedIdRef.current = selectedId
  }, [selectedId])

  /**
   * The highest sequence applied per conversation, for `Resync`.
   *
   * A ref rather than state: the connection reads it at reconnect time and must see the current
   * value, not the one captured when the handler was created. As state it would close over a stale
   * snapshot and ask the server for a gap that was already filled — or for one that has since grown.
   */
  const lastSeen = useRef<Record<string, number>>({})
  const connectionRef = useRef<ChatConnection | null>(null)

  const applyIncoming = useCallback((messages: readonly MessageResponse[]) => {
    for (const message of messages) {
      const current = lastSeen.current[message.conversationId] ?? 0
      if (message.seq > current) {
        lastSeen.current[message.conversationId] = message.seq
      }
    }

    // Appended rather than replaced, so a message that arrives while a different conversation is
    // open is still there when the reader switches to it. Merged by id downstream, which makes this
    // idempotent — required, because delivery is at-least-once and `Resync` re-sends deliberately.
    setIncoming((previous) => [...previous, ...messages])
  }, [])

  /**
   * Opens the connection once, for the life of the screen.
   *
   * Built inside the effect rather than in a `useMemo`. A memo is a performance hint that React is
   * free to discard, so constructing a socket in one risks opening a second connection and leaking
   * the first — and the handlers below capture nothing that changes, so there is nothing to
   * recompute anyway. Typing state is stored per conversation precisely so this does not have to be
   * rebuilt when the reader switches conversation; rebuilding would drop and re-establish the socket
   * on every click.
   */
  useEffect(() => {
    const connection = new ChatConnection({
      url: '/hubs/chat',
      accessTokenFactory: getAccessToken,
      lastSeenSeq: () => ({ ...lastSeen.current }),
      onMessages: applyIncoming,
      onMessageEdited: (message) => {
        applyIncoming([message])
      },
      onTypingChanged: (event) => {
        setTyping((previous) => ({
          ...previous,
          [event.conversationId]: event.employeeIds.filter((id) => id !== currentEmployeeId),
        }))
      },
      // US3 scenario 1: added to a group, and it appears without a refresh. The list refetches
      // rather than the event's own payload being spliced in, because the list projection carries
      // member and unread counts this event does not.
      onConversationCreated: () => {
        void queryClient.invalidateQueries({ queryKey: conversationsQueryKey })
      },
      // US3 scenario 3: removed, and access ends immediately. The open conversation is cleared
      // straight away rather than waiting for the list to refetch and quietly drop the row — a
      // reader mid-scroll should not keep reading a conversation they no longer belong to.
      onMembershipRevoked: (event) => {
        void queryClient.invalidateQueries({ queryKey: conversationsQueryKey })

        if (selectedIdRef.current === event.conversationId) {
          setSelectedId(null)
        }
      },
      onConnectionStateChanged: setConnected,
    })

    connectionRef.current = connection
    void connection.start()

    return () => {
      connectionRef.current = null
      void connection.stop()
    }
  }, [applyIncoming, currentEmployeeId, getAccessToken, queryClient])

  const startTyping = useCallback((conversationId: string) => {
    void connectionRef.current?.startTyping(conversationId)
  }, [])

  const stopTyping = useCallback((conversationId: string) => {
    void connectionRef.current?.stopTyping(conversationId)
  }, [])

  const [creatingGroup, setCreatingGroup] = useState(false)
  const [searching, setSearching] = useState(false)

  // The kind and history-visibility rule live on the conversation, not on any message — needed by
  // ConversationView for the group-only member panel and US3 scenario 4's history notice, and cheap
  // to ask for again here: TanStack Query already holds it from whichever list or create call
  // produced this id.
  const selectedConversation = useQuery({
    queryKey: ['conversations', selectedId],
    // `enabled` guards this from ever running with a null id; the fallback is only to satisfy the
    // parameter's type, never actually reached.
    queryFn: () => client.getConversation(selectedId ?? ''),
    enabled: selectedId !== null,
  })

  return (
    <div>
      <ConversationList client={client} selectedId={selectedId} onSelect={setSelectedId} />

      <button
        type="button"
        onClick={() => {
          setCreatingGroup((value) => !value)
        }}
      >
        {creatingGroup ? 'Cancel' : 'New group'}
      </button>

      <button
        type="button"
        onClick={() => {
          setSearching((value) => !value)
        }}
        aria-pressed={searching}
      >
        {searching ? 'Close search' : 'Search'}
      </button>

      {searching && (
        <SearchPanel
          api={client}
          onJumpTo={(conversationId) => {
            // Opens the conversation. Scrolling to the exact sequence is the transcript's job
            // and needs an anchor the virtualizer owns; opening the right conversation is the
            // half that can be done from here, and is what FR-031 asks for first.
            setSelectedId(conversationId)
            setSearching(false)
          }}
        />
      )}

      {creatingGroup && (
        <CreateGroupForm
          client={client}
          onCreated={(conversationId) => {
            setCreatingGroup(false)
            setSelectedId(conversationId)
          }}
        />
      )}

      {selectedId === null || !selectedConversation.data ? (
        <p>Choose a conversation.</p>
      ) : (
        <ConversationView
          conversationId={selectedId}
          currentEmployeeId={currentEmployeeId}
          client={client}
          connected={connected}
          typing={typing[selectedId] ?? []}
          names={{}}
          incoming={incoming}
          onStartTyping={startTyping}
          onStopTyping={stopTyping}
          kind={selectedConversation.data.kind}
          historyVisibility={selectedConversation.data.historyVisibility}
          mutedUntil={selectedConversation.data.mutedUntil}
        />
      )}
    </div>
  )
}
