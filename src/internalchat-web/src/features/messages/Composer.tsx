/**
 * T102 — the composer, with optimistic send through the offline queue.
 *
 * The component holds no send logic of its own. Everything that makes a send safe — the ULID key
 * assigned once, the ordering, the single-flight flush, the persistence across a reload — lives in
 * `OfflineQueue`, where it is unit-tested (T105). A composer that called the API directly would be a
 * second send path with none of those properties, and it would be the one people actually use.
 */

import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type ChangeEvent,
  type KeyboardEvent,
  type SyntheticEvent,
} from 'react'

import {
  ImageUpload,
  type ImageUploadHandle,
  type PendingAttachment,
  type UploadApi,
} from '../attachments/ImageUpload'
import { imageFilesFromPaste } from '../attachments/paste'
import type { OfflineQueue } from '../../lib/messages/offlineQueue'
import { MentionAutocomplete } from './MentionAutocomplete'
import { detectMentionQuery, type MentionCandidate } from './mentionQuery'

/** Matches `MessageBody.MaximumLength` on the server and `maxLength` in openapi.yaml. */
const MAXIMUM_BODY_LENGTH = 8000

/** How long after the last keystroke the typing signal is withdrawn. */
const TYPING_IDLE_MS = 3_000

interface ComposerProps {
  readonly conversationId: string
  readonly queue: OfflineQueue
  /** Called after a message is enqueued, so the caller can flush and re-render. */
  readonly onEnqueued: () => void
  // `| undefined` spelled out rather than `?:` alone. Under `exactOptionalPropertyTypes` an
  // optional property does not accept an explicit `undefined`, and a caller forwarding its own
  // optional handler is passing exactly that.
  readonly onStartTyping?: ((conversationId: string) => void) | undefined
  readonly onStopTyping?: ((conversationId: string) => void) | undefined
  /** Whether the transport is up. Only affects what the employee is told, never whether they can send. */
  readonly connected: boolean
  /**
   * Colleagues eligible for `@mention` (US3 scenario 5). Omitted or empty for a direct conversation,
   * where mentioning the one other participant would tell them nothing they do not already know.
   */
  readonly mentionCandidates?: readonly MentionCandidate[] | undefined
  /**
   * Reserves uploads. Omitted where attachments do not belong, in which case the attach
   * control is not rendered at all rather than rendered and refused.
   */
  readonly uploadApi?: UploadApi | undefined
}

/** The message input. */
export function Composer({
  conversationId,
  queue,
  onEnqueued,
  onStartTyping,
  onStopTyping,
  connected,
  mentionCandidates,
  uploadApi,
}: ComposerProps) {
  const [body, setBody] = useState('')
  const [attachments, setAttachments] = useState<readonly PendingAttachment[]>([])
  const [mentionQuery, setMentionQuery] = useState<string | null>(null)
  const typingTimer = useRef<number | null>(null)
  const typingActive = useRef(false)
  const textareaRef = useRef<HTMLTextAreaElement | null>(null)

  // Ids the reader picked from the popup, keyed by the exact `@Name` text inserted for them — so an
  // edit that removes the mention (backspacing over it) drops the id along with the text, rather
  // than silently notifying someone whose name no longer appears in the message.
  const insertedMentions = useRef<Map<string, string>>(new Map())

  // The paste lands on the textarea, which ImageUpload does not own, so the files are handed to
  // it through this handle rather than passed down as state.
  const uploader = useRef<ImageUploadHandle | null>(null)

  const stopTyping = useCallback(() => {
    if (typingActive.current) {
      typingActive.current = false
      onStopTyping?.(conversationId)
    }

    if (typingTimer.current !== null) {
      window.clearTimeout(typingTimer.current)
      typingTimer.current = null
    }
  }, [conversationId, onStopTyping])

  // Withdrawn on unmount and on switching conversations, or the previous conversation keeps showing
  // this employee as typing until the server's 10-second TTL expires.
  useEffect(() => stopTyping, [stopTyping])

  const noteTyping = useCallback(() => {
    if (!typingActive.current) {
      typingActive.current = true
      onStartTyping?.(conversationId)
    }

    if (typingTimer.current !== null) {
      window.clearTimeout(typingTimer.current)
    }

    typingTimer.current = window.setTimeout(stopTyping, TYPING_IDLE_MS)
  }, [conversationId, onStartTyping, stopTyping])

  const submit = useCallback(
    (event?: SyntheticEvent) => {
      event?.preventDefault()

      const trimmed = body.trim()

      // Uploaded and reserved, which is as far as a send can wait: FR-024 leaves the scan
      // running afterwards, and the transcript renders "scanning…" until it clears.
      const ready = attachments
        .filter((item) => item.status === 'uploaded' && item.attachmentId !== null)
        .flatMap((item) => (item.attachmentId === null ? [] : [item.attachmentId]))

      // A message that is only an attachment is a message. Requiring text would make sharing a
      // screenshot need a caption nobody wants to write.
      if (trimmed.length === 0 && ready.length === 0) {
        return
      }

      // The same ceiling the Send button is disabled by. Checked here as well because Enter does
      // not go through the button: without it, an over-long message is enqueued, refused by the
      // server as a 422, and then dropped by the queue as a permanent rejection — so the sender
      // watches their text vanish with nothing said. The visible error is already on screen; this
      // just stops the send that would discard it.
      if (trimmed.length > MAXIMUM_BODY_LENGTH) {
        return
      }

      // Only mentions whose exact inserted text still appears in the body are sent — the map is not
      // otherwise pruned when text is edited, so a since-deleted `@Name` must not still count.
      const mentions = [...insertedMentions.current.entries()]
        .filter(([inserted]) => body.includes(inserted))
        .map(([, employeeId]) => employeeId)

      // Enqueued, not sent. The input clears immediately and the message renders as pending, which
      // is what makes the composer feel instant on a slow connection — and it is honest, because the
      // queue really will deliver it.
      queue.enqueue(
        conversationId,
        trimmed,
        undefined,
        mentions.length > 0 ? mentions : undefined,
        ready.length > 0 ? ready : undefined,
      )
      setBody('')
      setAttachments([])
      setMentionQuery(null)
      insertedMentions.current = new Map()
      stopTyping()
      onEnqueued()
    },
    [attachments, body, conversationId, onEnqueued, queue, stopTyping],
  )

  const onChange = useCallback(
    (event: ChangeEvent<HTMLTextAreaElement>) => {
      setBody(event.target.value)
      noteTyping()
      setMentionQuery(detectMentionQuery(event.target.value, event.target.selectionStart))
    },
    [noteTyping],
  )

  const selectMention = useCallback(
    (candidate: MentionCandidate) => {
      const textarea = textareaRef.current
      if (!textarea) {
        return
      }

      const caretIndex = textarea.selectionStart
      const upToCaret = body.slice(0, caretIndex)
      const atIndex = upToCaret.lastIndexOf('@')
      if (atIndex === -1) {
        return
      }

      const inserted = `@${candidate.displayName} `
      const nextBody = body.slice(0, atIndex) + inserted + body.slice(caretIndex)

      insertedMentions.current.set(inserted, candidate.id)
      setBody(nextBody)
      setMentionQuery(null)

      // The caret has to be told where to go; React re-rendering the value alone leaves it wherever
      // the browser last put it, which after a programmatic change is the very end of the field.
      const nextCaret = atIndex + inserted.length
      requestAnimationFrame(() => {
        textarea.setSelectionRange(nextCaret, nextCaret)
        textarea.focus()
      })
    },
    [body],
  )

  const onKeyDown = useCallback(
    (event: KeyboardEvent<HTMLTextAreaElement>) => {
      // Enter sends, Shift+Enter is a newline. A textarea rather than an input precisely so a
      // multi-line message — a pasted stack trace, most often — stays readable.
      if (event.key === 'Enter' && !event.shiftKey) {
        event.preventDefault()
        submit()
      }
    },
    [submit],
  )

  const uploading = attachments.some(
    (item) => item.status !== 'uploaded' && item.status !== 'failed',
  )

  const sendable =
    body.trim().length > 0 ||
    attachments.some((item) => item.status === 'uploaded' && item.attachmentId !== null)

  const tooLong = body.trim().length > MAXIMUM_BODY_LENGTH

  return (
    <form onSubmit={submit}>
      <label htmlFor="composer-body">Message</label>

      <textarea
        id="composer-body"
        data-testid="composer"
        ref={textareaRef}
        value={body}
        onChange={onChange}
        onKeyDown={onKeyDown}
        onBlur={stopTyping}
        onPaste={(event) => {
          // The files are taken and the event is left alone, so a paste carrying both an image
          // and text still pastes the text — which is what a screenshot tool puts on the
          // clipboard, and refusing the text half would be a regression for everyone.
          const files = imageFilesFromPaste(event)

          if (files.length > 0) {
            uploader.current?.upload(files)
          }
        }}
        rows={2}
        // Not a `maxLength` attribute. Silently truncating a pasted message loses the end of it
        // without saying so; refusing to send and explaining why does not.
        aria-invalid={tooLong}
        aria-describedby={tooLong ? 'composer-error' : undefined}
      />

      {mentionQuery !== null && mentionCandidates && mentionCandidates.length > 0 && (
        <MentionAutocomplete
          query={mentionQuery}
          candidates={mentionCandidates}
          onSelect={selectMention}
        />
      )}

      {tooLong && (
        <p id="composer-error" role="alert">
          A message is at most {MAXIMUM_BODY_LENGTH.toLocaleString()} characters. This one is{' '}
          {body.trim().length.toLocaleString()}.
        </p>
      )}

      {uploadApi !== undefined && (
        <ImageUpload
          conversationId={conversationId}
          api={uploadApi}
          attachments={attachments}
          onChange={setAttachments}
          handleRef={uploader}
        />
      )}

      {/*
        Disabled while an upload is in flight rather than sending without it. The alternative —
        sending the text now and the attachment when it lands — produces two messages for one
        action, and the reader sees the caption before the picture.
      */}
      <button type="submit" disabled={!sendable || tooLong || uploading}>
        {uploading ? 'Uploading…' : 'Send'}
      </button>

      {/*
        Said plainly, and the composer stays usable. The queue holds the message and delivers it on
        reconnect (FR-018), so disabling the input while offline would take away the one thing that
        works — and quickstart V2 step 3 depends on being able to type three messages with the
        network down.
      */}
      {!connected && (
        <p role="status" data-testid="offline-notice">
          You are offline. Messages you send will be delivered when the connection returns.
        </p>
      )}
    </form>
  )
}
