/**
 * Ties the meeting pieces to a conversation: start, prompt, join, room, and screen share
 * (US8, US9 — FR-041 to FR-051).
 *
 * <b>The dynamic import is the whole reason this file exists rather than the room being rendered
 * directly.</b> `livekit-client` plus `@livekit/components-react` is larger than plan.md's entire
 * 300 KB initial-bundle budget. `./index.ts` is the `React.lazy` boundary that keeps it in its own
 * chunk, and this component is what finally references that boundary — up to now nothing did, so
 * Rollup tree-shook the whole feature out of the build and the chunk was never emitted at all.
 * `vite.config.ts` fails the build if a static import ever pulls the SDK back into the entry.
 *
 * <b>The prompt is deliberately not behind the boundary.</b> It renders for everyone in the
 * conversation the moment a meeting starts, and most of them will not join. Pulling the SDK down to
 * show a button would defeat the split for the majority to serve the minority.
 */
import { Suspense, useCallback, useState } from 'react'
import { Video } from 'lucide-react'

import type { MessagingClient, MeetingTokenResponse } from '../../lib/api/messages'
import { MeetingRoom } from './index'
import { JoinPrompt, type Meeting } from './JoinPrompt'
import { ScreenShareControl, ShareDisplacedNotice } from './ScreenShareControl'

interface MeetingPanelProps {
  readonly conversationId: string
  readonly client: MessagingClient
  /** The reader's own id, so a takeover notice naming them says "you" rather than a stranger. */
  readonly currentEmployeeId: string
}

/** Start, join, and hold a meeting for one conversation. */
export function MeetingPanel({ conversationId, client, currentEmployeeId }: MeetingPanelProps) {
  const [meeting, setMeeting] = useState<Meeting | null>(null)
  const [credential, setCredential] = useState<MeetingTokenResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [sharing, setSharing] = useState(false)
  const [displacedBy, setDisplacedBy] = useState<string | null>(null)

  const start = useCallback(() => {
    setBusy(true)
    setError(null)

    void (async () => {
      try {
        const started = await client.startMeeting(conversationId)

        setMeeting(started)
      } catch (cause) {
        // 503 here means meetings are unavailable — the platform ceiling (FR-044) or the media host
        // being down. Said as its own thing rather than a generic failure, because messaging is
        // unaffected and the person should not conclude the platform is down.
        setError(
          cause instanceof Error ? cause.message : 'Meetings are unavailable right now.',
        )
      } finally {
        setBusy(false)
      }
    })()
  }, [client, conversationId])

  const join = useCallback(
    (meetingId: string) => {
      setBusy(true)
      setError(null)

      void (async () => {
        try {
          setCredential(await client.joinMeeting(meetingId))
        } catch (cause) {
          setError(cause instanceof Error ? cause.message : 'Could not join the meeting.')
        } finally {
          setBusy(false)
        }
      })()
    },
    [client],
  )

  const leave = useCallback(() => {
    // The credential is dropped, not just the room unmounted. It is a bearer capability the media
    // server accepts on its face, so keeping it around after leaving serves no purpose and is one
    // more place it could be read from.
    setCredential(null)
    setSharing(false)
  }, [])

  const shareApi = {
    startShare: async (meetingId: string, scope: 'screen' | 'window') => {
      const result = await client.startShare(meetingId, scope)

      return { displacedEmployeeId: result.displacedEmployeeId }
    },
    stopShare: (meetingId: string) => client.stopShare(meetingId),
  }

  if (credential !== null && meeting !== null) {
    return (
      <section aria-label="Meeting" className="meeting-panel">
        <ShareDisplacedNotice
          displacedBy={displacedBy}
          onDismiss={() => {
            setDisplacedBy(null)
          }}
        />

        {/*
          The fallback is what someone sees for the second or so the chunk takes to arrive after
          they click join. Saying what is happening beats an empty panel that looks like a failure.
        */}
        <Suspense fallback={<p role="status">Connecting to the meeting…</p>}>
          <MeetingRoom
            token={credential.token}
            serverUrl={credential.mediaServerUrl}
            onLeave={leave}
            expiresAt={credential.expiresAt}
          />
        </Suspense>

        <ScreenShareControl
          meetingId={meeting.id}
          api={shareApi}
          isSharing={sharing}
          setCapture={(enabled) => {
            setSharing(enabled)
            return Promise.resolve()
          }}
          // The API reports who was displaced by id, and this client holds no directory for the
          // meeting. Naming the reader themselves is the one case worth resolving — "you took over
          // sharing from you" would be nonsense — and everyone else is "a colleague" until a
          // participant roster exists to look them up in.
          nameOf={(employeeId) => (employeeId === currentEmployeeId ? 'yourself' : 'a colleague')}
        />
      </section>
    )
  }

  return (
    <section aria-label="Meeting" className="meeting-panel">
      {meeting === null ? (
        <button type="button" className="btn-secondary" onClick={start} disabled={busy} data-testid="start-meeting">
          {busy ? <><Video size={16} /> Starting…</> : <><Video size={16} /> Start a meeting</>}
        </button>
      ) : (
        <JoinPrompt meeting={meeting} onJoin={join} joining={busy} />
      )}

      {error !== null && (
        <p role="alert" data-testid="meeting-error">
          {error} Messaging is unaffected.
        </p>
      )}
    </section>
  )
}
