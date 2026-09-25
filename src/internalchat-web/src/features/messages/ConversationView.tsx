/**
 * Wires one conversation together: history, real-time delivery, the offline queue, and typing.
 *
 * This is where the pieces meet, and the reconciliation rule lives here because it is a property of
 * the combination rather than of any one part: a message is identified by `clientMessageKey` while it
 * is pending and by `id` once confirmed, and the two must resolve to one bubble. Getting that wrong
 * shows every message the reader sent twice — once optimistically, once for real.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Bell, BellOff, Users } from 'lucide-react'

import type { MessagingClient, MessageResponse } from '../../lib/api/messages'
import { OfflineQueue, type QueuedMessage, type SendOutcome } from '../../lib/messages/offlineQueue'
import { GroupMembers } from '../conversations/GroupSettings'
import { MeetingPanel } from '../meetings/MeetingPanel'
import { HistoryNotice } from '../conversations/HistoryNotice'
import { membersQueryKey } from '../conversations/queryKeys'
import { Composer } from './Composer'
import { MessageList } from './MessageList'
import { TypingIndicator } from './TypingIndicator'

interface ConversationViewProps {
  readonly conversationId: string
  readonly currentEmployeeId: string
  readonly client: MessagingClient
  readonly connected: boolean
  readonly typing: readonly string[]
  readonly names: Readonly<Record<string, string>>
  readonly onStartTyping?: (conversationId: string) => void
  readonly onStopTyping?: (conversationId: string) => void
  /** Messages arriving over SignalR for this conversation. */
  readonly incoming: readonly MessageResponse[]
  readonly kind: 'direct' | 'group'
  readonly historyVisibility: 'from_join' | 'full'
  /** This employee's own mute for this conversation (FR-037). Access is unaffected either way. */
  readonly mutedUntil: string | null
}

/** One conversation, with its transcript and composer. */
export function ConversationView({
  conversationId,
  currentEmployeeId,
  client,
  connected,
  typing,
  names,
  onStartTyping,
  onStopTyping,
  incoming,
  kind,
  historyVisibility,
  mutedUntil,
}: ConversationViewProps) {
  const [messages, setMessages] = useState<readonly MessageResponse[]>([])
  const [pending, setPending] = useState<readonly QueuedMessage[]>([])
  const [hasOlder, setHasOlder] = useState(false)
  const [showMembers, setShowMembers] = useState(false)

  // Local and optimistic: muting is a personal notification preference, not something other
  // viewers of this conversation need to see, so there is no shared cache entry to coordinate with.
  const [muted, setMuted] = useState(mutedUntil !== null)

  // Adjusted during render rather than in an effect — this component is not remounted when
  // `conversationId` changes, so a `useState` initialiser alone would only ever apply to whichever
  // conversation happened to be first. Comparing against the last-seen id is the pattern React's
  // own docs give for "reset (or, here, resync) state when a prop changes" without an extra render.
  const [mutedForConversation, setMutedForConversation] = useState(conversationId)
  if (conversationId !== mutedForConversation) {
    setMutedForConversation(conversationId)
    setMuted(mutedUntil !== null)
  }

  const toggleMute = useCallback(() => {
    const next = !muted
    setMuted(next)

    // Ten years out is "muted until I say otherwise" — there is no dedicated "forever" value in
    // the contract, and a far future timestamp reads the same to every caller that checks it.
    void client.muteConversation(
      conversationId,
      next ? new Date(Date.now() + 365 * 10 * 24 * 60 * 60 * 1000).toISOString() : null,
    )
  }, [client, conversationId, muted])

  // Shared with GroupMembers below through the same TanStack Query key — one network request serves
  // both the member-management panel and the composer's mention candidates.
  const members = useQuery({
    queryKey: membersQueryKey(conversationId),
    queryFn: () => client.listMembers(conversationId),
    enabled: kind === 'group',
  })

  const mentionCandidates = useMemo(
    () =>
      (members.data ?? [])
        .filter((member) => member.employee.id !== currentEmployeeId)
        .map((member) => ({ id: member.employee.id, displayName: member.employee.displayName })),
    [members.data, currentEmployeeId],
  )

  /**
   * Applies messages from any source, keyed by id.
   *
   * Idempotent on purpose. Delivery is at-least-once over the wire and `Resync` deliberately
   * re-sends what a client may already hold, so the same message arrives more than once as a matter
   * of course — contracts/signalr-hub.md says so and expects the client to absorb it.
   */
  const apply = useCallback(
    (arriving: readonly MessageResponse[]) => {
      if (arriving.length === 0) {
        return
      }

      setMessages((current) => {
        const byId = new Map(current.map((m) => [m.id, m]))

        for (const message of arriving) {
          if (message.conversationId === conversationId) {
            byId.set(message.id, message)
          }
        }

        return [...byId.values()].sort((a, b) => a.seq - b.seq)
      })
    },
    [conversationId],
  )

  const queue = useMemo(
    () =>
      new OfflineQueue(async (message: QueuedMessage): Promise<SendOutcome> => {
        try {
          const result = await client.sendMessage(
            message.conversationId,
            message.clientMessageKey,
            message.body,
            message.mentions,
            message.attachmentIds,
          )

          // A replay is a success, not a conflict. The server returned the message this key already
          // produced, so applying it reconciles the optimistic bubble with the real one.
          apply([result.message])
          return { status: 'sent' }
        } catch (error) {
          const status = (error as { status?: number }).status ?? 0

          // 4xx other than 429 will not succeed on retry — a body that is too long stays too long.
          // Anything else is worth retrying: offline, a timeout, a 5xx, or a rate limit.
          return status >= 400 && status < 500 && status !== 429
            ? { status: 'rejected', reason: `The server refused this message (${String(status)}).` }
            : { status: 'retry' }
        }
      }),
    [apply, client],
  )

  useEffect(() => queue.subscribe(setPending), [queue])

  // Flushed whenever the transport comes back. The queue is single-flight, so this is safe to call
  // more often than necessary — and calling it too rarely is what leaves messages stuck.
  useEffect(() => {
    if (connected) {
      void queue.flush()
    }
  }, [connected, queue])

  const loadedFor = useRef<string | null>(null)

  useEffect(() => {
    if (loadedFor.current === conversationId) {
      return
    }

    loadedFor.current = conversationId
    setMessages([])

    void (async () => {
      const page = await client.getHistory(conversationId)
      apply(page.items)
      setHasOlder(page.hasMore)
    })()
  }, [apply, client, conversationId])

  const loadOlder = useCallback(() => {
    const lowest = messages[0]?.seq
    if (lowest === undefined) {
      return
    }

    void (async () => {
      const page = await client.getHistory(conversationId, lowest)
      apply(page.items)
      setHasOlder(page.hasMore)
    })()
  }, [apply, client, conversationId, messages])

  /**
   * The transcript: fetched history and send results, merged with whatever has arrived live.
   *
   * Derived during render rather than copied into state by an effect. Copying a prop into state is
   * the antipattern React's own guidance warns about — it causes a second render for every arriving
   * message and leaves two sources of truth for the same list, which then disagree the first time
   * one of them is updated and the other is not. Merging by id here is also idempotent, which the
   * at-least-once delivery in contracts/signalr-hub.md requires of any client.
   */
  const visible = useMemo(() => {
    const byId = new Map(messages.map((m) => [m.id, m]))

    for (const message of incoming) {
      if (message.conversationId === conversationId) {
        byId.set(message.id, message)
      }
    }

    return [...byId.values()].sort((a, b) => a.seq - b.seq)
  }, [messages, incoming, conversationId])

  // Advances read position to the newest visible message (FR-036). Runs whenever the transcript
  // gains a message — including one that arrived live while this conversation stays open — so the
  // badge clears for this employee's other devices without the reader doing anything beyond
  // looking at the screen they already have open.
  const highestSeq = visible.at(-1)?.seq
  useEffect(() => {
    if (highestSeq === undefined) {
      return
    }

    void client.markRead(conversationId, highestSeq)
  }, [client, conversationId, highestSeq])

  // Pending sends whose message has since arrived are dropped from the optimistic list. The queue
  // removes them when the send resolves, but a message that arrived over SignalR before the HTTP
  // response did would otherwise render twice for a moment.
  const confirmedKeys = new Set(visible.map((m) => m.clientMessageKey))
  const stillPending = pending.filter((p) => !confirmedKeys.has(p.clientMessageKey))

  return (
    <section aria-label="Conversation" className="chat-view-container">
      <div className="chat-view-header">
        <div className="chat-view-actions">
          <button type="button" className="btn-secondary" onClick={toggleMute} aria-pressed={muted} data-testid="mute-toggle">
            {muted ? <><Bell size={16} /> Unmute Notifications</> : <><BellOff size={16} /> Mute Notifications</>}
          </button>
        </div>
        
        {kind === 'group' && (
          <div className="group-members-container">
            <button type="button" className="btn-secondary" onClick={() => setShowMembers(v => !v)}>
              <Users size={16} /> Members
            </button>
            {showMembers && (
              <GroupMembers
                conversationId={conversationId}
                client={client}
                currentEmployeeId={currentEmployeeId}
              />
            )}
          </div>
        )}
      </div>

      <div className="chat-notices">
        {kind === 'group' && <HistoryNotice historyVisibility={historyVisibility} />}
        <MeetingPanel
          conversationId={conversationId}
          client={client}
          currentEmployeeId={currentEmployeeId}
        />
      </div>

      <div className="chat-messages-area">
        <MessageList
          messages={visible}
          pending={stillPending}
          currentEmployeeId={currentEmployeeId}
          onLoadOlder={loadOlder}
          hasOlder={hasOlder}
          refreshAttachment={(attachmentId) => client.getAttachment(attachmentId)}
        />
      </div>

      <div className="chat-composer-area">
        <TypingIndicator typing={typing} names={names} />
        <Composer
          conversationId={conversationId}
          queue={queue}
          connected={connected}
          onEnqueued={() => void queue.flush()}
          onStartTyping={onStartTyping}
          onStopTyping={onStopTyping}
          mentionCandidates={mentionCandidates}
          uploadApi={client}
        />
      </div>
    </section>
  )
}
