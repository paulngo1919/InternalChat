/**
 * T157 — inline previews and the lightbox (FR-021).
 *
 * The component renders three states, and the middle one is the reason it exists at all: an
 * attachment that has been uploaded but not yet scanned. FR-024 makes that state genuinely
 * unavoidable — nothing is retrievable before a clean verdict — so a preview that only knew "image"
 * and "broken image" would show a broken image every time, for a second or two, on every send.
 * Saying "scanning…" turns a correct wait into an explicable one.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'

import type { AttachmentResponse } from '../../lib/api/messages'
import { formatBytes } from './fileConstraints'
import { VideoPlayer } from './VideoPlayer'

interface ImagePreviewProps {
  readonly attachments: readonly AttachmentResponse[]
  /**
   * Re-fetches one attachment's metadata. Supplied by the caller so this component owns no client.
   * Omitted in a context that cannot poll, where a pending attachment simply stays pending until
   * the next render from elsewhere.
   */
  readonly refresh?: ((attachmentId: string) => Promise<AttachmentResponse>) | undefined
}

/** How long between scan-status polls. */
const POLL_INTERVAL_MS = 2_000

/** How long to keep polling before giving up and letting the person reload. */
const POLL_TIMEOUT_MS = 120_000

/** Renders a message's attachments inline. */
export function ImagePreview({ attachments, refresh }: ImagePreviewProps) {
  // Polled results are an OVERLAY on the props, not a copy of them. The obvious shape — copy the
  // prop into state and re-sync it in an effect — has two defects: it renders the stale copy for
  // one frame whenever the parent sends new data, and it makes the parent's value and this
  // component's disagree for as long as that takes. Merging during render means the props are
  // always authoritative and the overlay only ever supplies a newer scan status.
  const [polled, setPolled] = useState<ReadonlyMap<string, AttachmentResponse>>(new Map())
  const [lightboxId, setLightboxId] = useState<string | null>(null)

  const resolved = useMemo(
    () => attachments.map((item) => polled.get(item.id) ?? item),
    [attachments, polled],
  )

  const lightbox = resolved.find((item) => item.id === lightboxId) ?? null

  // Polls only while something is actually pending, and stops as soon as nothing is. A poll that
  // ran unconditionally would put one request every two seconds per rendered message on a
  // conversation full of images.
  // A primitive key rather than the array itself, and this is load-bearing rather than tidiness.
  // Every poll calls `setPolled` with a fresh Map, which gives `resolved` a new identity, which
  // restarted the effect below and with it the deadline — so the timeout could never be reached and
  // a stuck scan really was polled forever, which is the exact failure the timeout exists to
  // prevent. Keyed on which attachments are pending, the effect only restarts when that set
  // actually changes.
  const pendingIds = useMemo(
    () =>
      resolved
        .filter((item) => item.scanStatus === 'pending')
        .map((item) => item.id)
        .join(','),
    [resolved],
  )

  useEffect(() => {
    if (refresh === undefined || pendingIds === '') {
      return
    }

    const ids = pendingIds.split(',')

    let cancelled = false
    const startedAt = Date.now()

    const timer = setInterval(() => {
      if (Date.now() - startedAt > POLL_TIMEOUT_MS) {
        // A scan this slow means something is wrong with the scanner, not with this file. Polling
        // forever would keep a tab busy indefinitely for an answer that is not coming.
        clearInterval(timer)
        return
      }

      for (const id of ids) {
        void refresh(id).then(
          (updated) => {
            if (!cancelled) {
              setPolled((current) => new Map(current).set(updated.id, updated))
            }
          },
          () => {
            // A failed poll is not worth surfacing: the next tick retries, and an error banner for
            // a transient metadata read would be noisier than the wait it describes.
          },
        )
      }
    }, POLL_INTERVAL_MS)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [pendingIds, refresh])

  const close = useCallback(() => {
    setLightboxId(null)
  }, [])

  useEffect(() => {
    if (lightbox === null) {
      return
    }

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        close()
      }
    }

    window.addEventListener('keydown', onKey)

    return () => {
      window.removeEventListener('keydown', onKey)
    }
  }, [lightbox, close])

  if (resolved.length === 0) {
    return null
  }

  return (
    <>
      <ul className="attachments">
        {resolved.map((attachment) => (
          <li key={attachment.id} className={`attachment is-${attachment.scanStatus}`}>
            <AttachmentBody
              attachment={attachment}
              onOpen={() => {
                setLightboxId(attachment.id)
              }}
            />
          </li>
        ))}
      </ul>

      {lightbox?.contentUrl != null && (
        // Modal rather than a new tab: opening the content URL directly would work — it is
        // authorized per request — but it leaves the conversation, and a person clicking a
        // screenshot expects to come back to it.
        <div className="lightbox" role="dialog" aria-modal="true" aria-label={lightbox.fileName}>
          {/*
            A button rather than a click handler on the backdrop div. Clicking outside to close is
            expected, but a div that responds to a mouse and not to a keyboard is unusable without
            one — and Escape is already handled above, so this only has to cover the pointer.
          */}
          <button
            type="button"
            className="lightbox__backdrop"
            onClick={close}
            aria-label="Close"
            tabIndex={-1}
          />

          <img src={lightbox.contentUrl} alt={lightbox.fileName} className="lightbox__image" />

          <button type="button" className="lightbox__close" onClick={close}>
            Close
          </button>
        </div>
      )}
    </>
  )
}

function AttachmentBody({
  attachment,
  onOpen,
}: {
  readonly attachment: AttachmentResponse
  readonly onOpen: () => void
}) {
  if (attachment.scanStatus === 'pending') {
    return (
      <span className="attachment__pending" aria-live="polite">
        Scanning {attachment.fileName}…
      </span>
    )
  }

  if (attachment.scanStatus === 'infected') {
    // Stated plainly. The person may have been expecting this file, and silence would look like a
    // failed upload rather than a deliberate block (FR-024).
    return (
      <span className="attachment__blocked" role="alert">
        {attachment.fileName} was blocked by a malware scan.
      </span>
    )
  }

  if (attachment.scanStatus === 'failed' || attachment.contentUrl === null) {
    return (
      <span className="attachment__failed">
        {attachment.fileName} could not be scanned and is not available.
      </span>
    )
  }

  if (attachment.kind === 'video') {
    // Delegated rather than inlined. VideoPlayer owns preload="metadata", the poster fallback, and
    // the unplayable-format path — duplicating any of that here is how one of the two copies
    // quietly loses it.
    return <VideoPlayer attachment={attachment} />
  }

  return (
    <button type="button" className="attachment__image-button" onClick={onOpen}>
      <img
        src={attachment.contentUrl}
        // The uploader's file name. Better than nothing and better than a generic "image" — but it
        // is not a description, which is why the lightbox repeats it as the dialog's label rather
        // than pretending this is alt text someone wrote.
        alt={attachment.fileName}
        className="attachment__image"
        loading="lazy"
      />
      <span className="attachment__meta">
        {attachment.fileName} · {formatBytes(attachment.byteSize)}
      </span>
    </button>
  )
}
