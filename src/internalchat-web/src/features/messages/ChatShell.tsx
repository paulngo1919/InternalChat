/**
 * The messaging screen: conversation list, transcript, composer, and the live connection.
 *
 * This is the only place that owns a `ChatConnection`. One connection per tab, not per conversation:
 * the hub delivers for every conversation the employee belongs to, and opening one per open
 * conversation would multiply 7,000 concurrent connections by however many tabs people keep.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'

import { Plus, Search } from 'lucide-react'

import {
  createMessagingClient,
  type ConversationPage,
  type MessageResponse,
} from '../../lib/api/messages'
import { readHubBaseUrl } from '../../lib/auth/config'
import { ChatConnection, type ChatTransport } from '../../lib/realtime/chatConnection'
import { DeliveryTelemetry } from '../../lib/realtime/deliveryTelemetry'
import { ConversationList } from '../conversations/ConversationList'
import { applyIncomingToConversationList } from '../conversations/conversationListCache'
import { CreateGroupForm } from '../conversations/GroupSettings'
import { conversationsQueryKey } from '../conversations/queryKeys'
import { SearchPanel } from '../search/SearchPanel'
import { ConversationView } from './ConversationView'
import { addTombstone } from './messageStore'

interface ChatShellProps {
  /** Issues authorized requests. Comes from the API client so the token handling is shared. */
  readonly authorized: (path: string, init?: RequestInit) => Promise<Response>
  readonly getAccessToken: () => Promise<string | null>
  readonly currentEmployeeId: string
}

/** Shared so an untouched conversation does not hand ConversationView a new array every render. */
const NO_DELETIONS: readonly string[] = []

/** The messaging screen. */
export function ChatShell({ authorized, getAccessToken, currentEmployeeId }: ChatShellProps) {
  const [client] = useState(() => createMessagingClient(authorized))

  // 002 FR-011 — what delivery felt like here, reported in aggregate once a minute.
  const [telemetry] = useState(() => new DeliveryTelemetry(authorized))
  const queryClient = useQueryClient()

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [incoming, setIncoming] = useState<readonly MessageResponse[]>([])
  // Per conversation, bounded (messageStore.TOMBSTONE_LIMIT). Kept here, beside `incoming`, because
  // the delete can arrive while its conversation is not the one on screen.
  const [deleted, setDeleted] = useState<Readonly<Record<string, readonly string[]>>>({})
  const [connected, setConnected] = useState(false)
  const [transport, setTransport] = useState<ChatTransport>('webSockets')
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

    // 002 FR-006: the list's unread badge and preview move with the message, not with the list's
    // next refetch. Patched in place; only a conversation the cache has never seen costs a refetch.
    let unknownConversation = false
    queryClient.setQueryData<ConversationPage>(conversationsQueryKey, (page) => {
      if (page === undefined) {
        return page
      }

      const patch = applyIncomingToConversationList(page, messages, {
        openConversationId: selectedIdRef.current,
        currentEmployeeId,
      })
      unknownConversation = patch.unknownConversation
      return patch.page
    })

    if (unknownConversation) {
      void queryClient.invalidateQueries({ queryKey: conversationsQueryKey, exact: true })
    }
  }, [currentEmployeeId, queryClient])

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
      url: readHubBaseUrl(import.meta.env),
      accessTokenFactory: getAccessToken,
      lastSeenSeq: () => ({ ...lastSeen.current }),
      onMessages: applyIncoming,
      onMessageEdited: (message) => {
        applyIncoming([message])
      },
      // Before 002 this event had no handler, so a colleague's deletion stayed on screen until the
      // next reload. It also has to stick: with concurrent fan-out, the original send can arrive
      // after the delete (hub contract 1.1.0, tombstone rule).
      onMessageDeleted: (event) => {
        setDeleted((previous) => ({
          ...previous,
          [event.conversationId]: addTombstone(previous[event.conversationId] ?? [], event.messageId),
        }))
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
      onTransportChanged: (next) => {
        setTransport(next)
        telemetry.setTransport(next)
      },
      onLiveMessage: (message) => {
        telemetry.recordDelivery(message.sentAt)
      },
    })

    telemetry.start()

    connectionRef.current = connection
    connection.start().catch((error: Error) => {
      if (error.name !== 'AbortError' && !error.message?.includes('stopped during negotiation')) {
        console.error('SignalR connection failed:', error)
      }
    })

    return () => {
      connectionRef.current = null
      telemetry.stop()
      void connection.stop()
    }
  }, [applyIncoming, currentEmployeeId, getAccessToken, queryClient, telemetry])

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
    <div className="chat-shell">
      {transport !== 'webSockets' && (
        // 002 FR-010. Non-blocking and polite: nothing is broken, but messages arrive more slowly,
        // and the employee should hear that from the app rather than conclude chat is just slow.
        <p className="connection-degraded" role="status" data-testid="connection-degraded">
          Limited connection — messages may arrive more slowly.
        </p>
      )}
      <div className="chat-sidebar">
        <ConversationList client={client} selectedId={selectedId} onSelect={setSelectedId} />

        <div className="sidebar-actions">
          <button
            type="button"
            className="btn-primary"
            onClick={() => {
              setCreatingGroup((value) => {
                if (!value) setSearching(false)
                return !value
              })
            }}
          >
            {creatingGroup ? 'Cancel' : <><Plus size={16} /> New group</>}
          </button>

          <button
            type="button"
            className="btn-secondary"
            onClick={() => {
              setSearching((value) => {
                if (!value) setCreatingGroup(false)
                return !value
              })
            }}
            aria-pressed={searching}
          >
            {searching ? 'Close search' : <><Search size={16} /> Search</>}
          </button>
        </div>

        {searching && (
          <SearchPanel
            api={client}
            onJumpTo={(conversationId) => {
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
      </div>

      <div className="chat-main">
        {selectedId === null || !selectedConversation.data ? (
          <div className="empty-state">
            <p>Choose a conversation to start chatting.</p>
          </div>
        ) : (
          <ConversationView
            conversationId={selectedId}
            currentEmployeeId={currentEmployeeId}
            client={client}
            connected={connected}
            typing={typing[selectedId] ?? []}
            names={{}}
            incoming={incoming}
            deletedIds={deleted[selectedId] ?? NO_DELETIONS}
            onSendTimed={(startedAt, finishedAt, sentAt) => {
              telemetry.observeRoundTrip(startedAt, finishedAt, sentAt)
            }}
            onStartTyping={startTyping}
            onStopTyping={stopTyping}
            kind={selectedConversation.data.kind}
            historyVisibility={selectedConversation.data.historyVisibility}
            mutedUntil={selectedConversation.data.mutedUntil}
          />
        )}
      </div>
    </div>
  )
}
