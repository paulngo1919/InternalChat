/**
 * T204 — the enlarge control and the "you are viewing X's share" indicator (FR-049, FR-050).
 *
 * Both exist for the same reason and it is not decoration:
 *
 * - **The indicator** is FR-050's "visible rule" from the *viewer's* side. When sharing changes
 *   hands the picture changes and nothing else does; without a name on it, a viewer cannot tell a
 *   handover from the presenter switching applications.
 * - **Enlarging** is FR-049. "Legible enough to read standard document text" is partly a media
 *   question — the SFU forwards what was captured — and partly a layout one: a 1080p document
 *   tiled next to five faces in a 300-pixel column is unreadable no matter how good the stream is.
 */

import { useCallback, useEffect, useState, type ReactNode } from 'react'

interface SharedViewProps {
  /** Who is sharing, or `null` when nobody is. */
  readonly presenterName: string | null
  /** The share's video element, supplied by the meeting room, which owns the SDK. */
  readonly children: ReactNode
}

/** Frames a screen share with its attribution and an enlarge control. */
export function SharedView({ presenterName, children }: SharedViewProps) {
  const [enlarged, setEnlarged] = useState(false)

  const collapse = useCallback(() => {
    setEnlarged(false)
  }, [])

  // Escape leaves the enlarged view, matching the lightbox and every other full-bleed surface in
  // the app. Registered only while enlarged, so it does not compete for the key otherwise.
  useEffect(() => {
    if (!enlarged) {
      return
    }

    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        collapse()
      }
    }

    window.addEventListener('keydown', onKey)

    return () => {
      window.removeEventListener('keydown', onKey)
    }
  }, [enlarged, collapse])

  // Nothing is being shared. Rendering an empty frame would leave a hole in the layout that reads
  // as a broken video.
  if (presenterName === null) {
    return null
  }

  return (
    <figure
      className={enlarged ? 'shared-view is-enlarged' : 'shared-view'}
      data-testid="shared-view"
    >
      {children}

      <figcaption className="shared-view__caption">
        {/*
          FR-050 from the viewer's side. When sharing changes hands the picture changes and nothing
          else does — without the name, a handover is indistinguishable from the presenter
          switching applications.
        */}
        <span data-testid="share-presenter">You are viewing {presenterName}&apos;s screen</span>

        <button
          type="button"
          onClick={() => {
            setEnlarged((current) => !current)
          }}
          aria-pressed={enlarged}
          data-testid="toggle-enlarge-share"
        >
          {enlarged ? 'Exit full view' : 'Enlarge'}
        </button>
      </figcaption>
    </figure>
  )
}
