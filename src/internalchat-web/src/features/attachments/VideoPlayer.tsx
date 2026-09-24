/**
 * T177 — the video player, with poster and seek (FR-022).
 *
 * Almost all of "plays without downloading the whole file" is decided on the server: the codec
 * allow-list means no transcoding, the faststart remux puts the index at the front, and nginx
 * serves byte ranges. What is left for the client is small and easy to get wrong.
 *
 * `preload="metadata"` is the line that matters. The default, `preload="auto"`, tells the browser
 * to start buffering the whole file as soon as the element mounts — so a conversation with six
 * videos in view begins six 400 MB downloads nobody asked for, and FR-022 is defeated by the
 * markup rather than by the transport.
 */

import { useCallback, useRef, useState } from 'react'

import type { AttachmentResponse } from '../../lib/api/messages'
import { formatBytes, formatDuration } from './fileConstraints'

interface VideoPlayerProps {
  readonly attachment: AttachmentResponse
}

/** Renders a clean video attachment. */
export function VideoPlayer({ attachment }: VideoPlayerProps) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const [failed, setFailed] = useState(false)

  const onError = useCallback(() => {
    // The browser could not play it. This should be unreachable — the codec allow-list is enforced
    // by probing the bitstream after upload (T175) — so reaching it means a format slipped through,
    // and saying so plainly is more useful than a silently blank rectangle.
    setFailed(true)
  }, [])

  if (attachment.contentUrl === null) {
    return null
  }

  if (failed) {
    return (
      <div className="attachment__video-failed" role="alert">
        <p>{attachment.fileName} cannot be played in this browser.</p>

        {/*
          A direct link to the same authorized route. Following it re-checks membership exactly as
          the player's own request does — it is a route, not a capability — so offering it takes
          nothing away from FR-025.
        */}
        <a href={attachment.contentUrl} download={attachment.fileName}>
          Download it instead ({formatBytes(attachment.byteSize)})
        </a>
      </div>
    )
  }

  return (
    <figure className="attachment__video">
      <video
        ref={videoRef}
        controls
        // THE line. `auto` would start buffering every video in the conversation on mount.
        preload="metadata"
        // Poster if the scan consumer extracted one; otherwise the browser shows its own first
        // frame once metadata arrives. Never a placeholder image — a generic film icon tells the
        // person less than the actual frame does.
        {...(attachment.posterUrl !== null ? { poster: attachment.posterUrl } : {})}
        onError={onError}
        data-testid="attachment-video"
      >
        <source src={attachment.contentUrl} type={attachment.contentType} />

        {/*
          An empty captions track. Uploaded clips carry no caption file and the platform generates
          none, so this declares the absence rather than claiming captions exist. If captioning is
          ever added, this is the element that gains a `src`.
        */}
        <track kind="captions" />
      </video>

      <figcaption className="attachment__video-meta">
        {attachment.fileName} · {formatBytes(attachment.byteSize)}
        {attachment.durationSeconds !== null && ` · ${formatDuration(attachment.durationSeconds)}`}
      </figcaption>
    </figure>
  )
}
