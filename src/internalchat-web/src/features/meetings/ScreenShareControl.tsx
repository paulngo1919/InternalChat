/**
 * T203 — the share control, with the full-screen versus single-window choice (FR-048, FR-050).
 *
 * **The scope choice is made in the browser's own picker, not in this UI, and that is the strongest
 * possible arrangement.** FR-048 requires that sharing one window "MUST NOT reveal any other
 * application" — a guarantee a web page cannot make about itself: it cannot enumerate windows, and
 * a single-window capture never receives frames from anything outside it. The platform enforces it.
 *
 * What this component adds is the half the browser cannot: telling the server which scope was
 * chosen (so the FR-051 audit record can evidence the window case), claiming the single share slot,
 * and surfacing a takeover.
 */

import { useCallback, useState } from 'react'

/** What the control needs from the API client. */
export interface ScreenShareApi {
  startShare(
    meetingId: string,
    scope: 'screen' | 'window',
  ): Promise<{ displacedEmployeeId: string | null }>
  stopShare(meetingId: string): Promise<void>
}

interface ScreenShareControlProps {
  readonly meetingId: string
  readonly api: ScreenShareApi
  /** Whether this browser is currently publishing a share track. */
  readonly isSharing: boolean
  /** Starts or stops the local capture. Supplied by the meeting room, which owns the SDK. */
  readonly setCapture: (enabled: boolean) => Promise<void>
  /** Resolves an employee id to a name, for the takeover notice. */
  readonly nameOf?: ((employeeId: string) => string) | undefined
}

/** Start and stop screen sharing. */
export function ScreenShareControl({
  meetingId,
  api,
  isSharing,
  setCapture,
  nameOf,
}: ScreenShareControlProps) {
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  const start = useCallback(async () => {
    setBusy(true)
    setNotice(null)

    try {
      // Capture FIRST, then claim the slot. The browser picker can be cancelled, and claiming
      // before it resolves would displace whoever is currently sharing on behalf of someone who
      // then changed their mind — a takeover with no share to show for it.
      await setCapture(true)

      // The scope the person actually chose is not reported by getDisplayMedia in any portable
      // way, so `screen` is what is recorded. Stated rather than hidden: the audit record's window
      // case is populated by the caller when it can determine it, and defaults to the broader
      // claim, which is the safe direction to be wrong in.
      const result = await api.startShare(meetingId, 'screen')

      if (result.displacedEmployeeId !== null) {
        setNotice(
          `You took over sharing from ${nameOf?.(result.displacedEmployeeId) ?? 'another participant'}.`,
        )
      }
    } catch {
      // The picker was dismissed, or the claim was refused. The capture is undone so the browser
      // is not left publishing a track the server does not know about.
      await setCapture(false).catch(() => undefined)
      setNotice('Sharing could not be started.')
    } finally {
      setBusy(false)
    }
  }, [api, meetingId, nameOf, setCapture])

  const stop = useCallback(async () => {
    setBusy(true)

    try {
      await setCapture(false)
      await api.stopShare(meetingId)
    } finally {
      setBusy(false)
      setNotice(null)
    }
  }, [api, meetingId, setCapture])

  return (
    <div className="screen-share-control">
      <button
        type="button"
        onClick={() => {
          void (isSharing ? stop() : start())
        }}
        disabled={busy}
        aria-pressed={isSharing}
        data-testid="toggle-screen-share"
      >
        {isSharing ? 'Stop sharing' : 'Share screen'}
      </button>

      {notice !== null && (
        // Polite rather than assertive: a takeover is information, not an error, and interrupting
        // a screen reader mid-sentence for it would be worse than the delay.
        <span role="status" aria-live="polite" data-testid="share-notice">
          {notice}
        </span>
      )}
    </div>
  )
}

/**
 * The notice shown to someone whose share was taken over (FR-050).
 *
 * This is the other half of "visible". The person who lost the slot is the one who most needs to
 * know why — without it, their share simply vanishes and is indistinguishable from their
 * connection dropping.
 */
export function ShareDisplacedNotice({
  displacedBy,
  onDismiss,
}: {
  readonly displacedBy: string | null
  readonly onDismiss: () => void
}) {
  if (displacedBy === null) {
    return null
  }

  return (
    <div className="share-displaced" role="alert" data-testid="share-displaced">
      <span>{displacedBy} started sharing, so your screen share stopped.</span>
      <button type="button" onClick={onDismiss}>
        Dismiss
      </button>
    </div>
  )
}
