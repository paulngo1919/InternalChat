/**
 * T101 — the virtualized message list.
 *
 * Virtualized because a conversation is not bounded. A year of an active group is tens of thousands
 * of messages, and rendering them all costs memory proportional to history rather than to the
 * viewport — which shows up as a tab that slows down the longer it stays open, on exactly the
 * conversations people use most.
 *
 * `react-virtuoso` rather than a hand-rolled windowing loop: it handles variable-height rows, which
 * a chat transcript is by nature, and the "stick to the bottom unless the reader has scrolled up"
 * behaviour that people notice immediately when it is wrong.
 */

import { Virtuoso } from 'react-virtuoso'

import { ImagePreview } from '../attachments/ImagePreview'
import type { AttachmentResponse, MessageResponse } from '../../lib/api/messages'
import type { QueuedMessage } from '../../lib/messages/offlineQueue'

interface MessageListProps {
  /** Confirmed messages, ascending by `seq`. */
  readonly messages: readonly MessageResponse[]
  /** Sends accepted by the composer but not yet acknowledged, rendered after the confirmed ones. */
  readonly pending: readonly QueuedMessage[]
  readonly currentEmployeeId: string
  /** Asks for an older page. Called when the reader reaches the top (FR-013). */
  readonly onLoadOlder: () => void
  readonly hasOlder: boolean
  /**
   * Re-reads one attachment's scan status, so a pending preview can resolve itself.
   *
   * Passed down rather than reached for, so this component owns no client — and omitted where
   * polling is not wanted, in which case a pending attachment simply stays pending.
   */
  readonly refreshAttachment?: ((attachmentId: string) => Promise<AttachmentResponse>) | undefined
}

/** One row: either a confirmed message or a still-sending one. */
type Row =
  | { readonly kind: 'message'; readonly key: string; readonly message: MessageResponse }
  | { readonly kind: 'pending'; readonly key: string; readonly message: QueuedMessage }

/** A deleted message renders as a tombstone, never as an empty bubble. */
function MessageBody({
  message,
  refreshAttachment,
}: {
  readonly message: MessageResponse
  readonly refreshAttachment?:
    | ((attachmentId: string) => Promise<AttachmentResponse>)
    | undefined
}) {
  if (message.deletedAt !== null) {
    // The row survives so ordering and sequence continuity hold (data-model.md); the content does
    // not. Saying so is better than an empty bubble, which reads as a rendering bug.
    return <em data-testid="tombstone">This message was deleted.</em>
  }

  return (
    <>
      <span>{message.body}</span>
      {message.editedAt !== null && <small data-testid="edited-marker"> (edited)</small>}

      {/*
        Below the text, and only when there is something to show. A message with no attachments
        must not gain an empty element — every message in a conversation would carry one.
      */}
      {message.attachments.length > 0 && (
        <ImagePreview attachments={message.attachments} refresh={refreshAttachment} />
      )}
    </>
  )
}

/** The transcript, virtualized and ordered by server sequence. */
export function MessageList({
  messages,
  pending,
  currentEmployeeId,
  onLoadOlder,
  hasOlder,
  refreshAttachment,
}: MessageListProps) {
  // Sorted by `seq`, never by `sentAt`. FR-012 makes the server sequence the ordering authority, and
  // contracts/signalr-hub.md requires clients to apply by `seq` rather than arrival time — a message
  // recovered by Resync arrives after one pushed live but belongs before it.
  const ordered = [...messages].sort((a, b) => a.seq - b.seq)

  const rows: Row[] = [
    ...ordered.map((message): Row => ({ kind: 'message', key: message.id, message })),

    // Pending sends go last regardless of sequence: they have none yet. Keyed by
    // clientMessageKey, which is the same key the confirmed message will carry — so when the
    // acknowledgement arrives the optimistic row is replaced rather than duplicated.
    ...pending.map((message): Row => ({
      kind: 'pending',
      key: message.clientMessageKey,
      message,
    })),
  ]

  return (
    <Virtuoso
      style={{ flex: 1, overflowX: 'hidden' }}
      data={rows}
      // Anchors the view to the newest message and keeps it there as messages arrive — unless the
      // reader has scrolled up, in which case it leaves them where they are. Yanking somebody back
      // to the bottom mid-read is the single most irritating thing a chat list can do.
      followOutput="auto"
      initialTopMostItemIndex={Math.max(rows.length - 1, 0)}
      startReached={() => {
        if (hasOlder) {
          onLoadOlder()
        }
      }}
      // A stable key per row. Without it, virtuoso reuses DOM nodes by index and a prepended page of
      // history makes every visible message appear to change content at once.
      computeItemKey={(_, row) => row.key}
      itemContent={(_, row) => {
        const mine =
          row.kind === 'message'
            ? row.message.authorId === currentEmployeeId
            : // A pending message is always the current employee's — nobody else's send is in this
              // browser's queue.
              true

        return (
          <article
            className={`message-row ${mine ? 'message-mine' : 'message-theirs'}`}
            data-testid={row.kind === 'pending' ? 'pending-message' : 'message'}
            data-mine={mine}
          >
            <div className="message-bubble">
              {row.kind === 'message' ? (
                <MessageBody message={row.message} refreshAttachment={refreshAttachment} />
              ) : (
                <>
                  <span>{row.message.body}</span>
                  <small className="pending-indicator" aria-live="polite"> Sending…</small>
                </>
              )}
            </div>
          </article>
        )
      }}
    />
  )
}
