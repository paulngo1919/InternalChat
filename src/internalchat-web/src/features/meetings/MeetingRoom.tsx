/**
 * T194 — the meeting room: participant tiles, mute, camera, and screen share
 * (FR-042, FR-045, FR-048, FR-050).
 *
 * **This file is the only place the LiveKit SDK is imported, and that is load-bearing.** It is
 * reached exclusively through `./index.ts`'s dynamic `import()`, which is what makes Rollup emit it
 * as a separate chunk. A static import of this module from anywhere in the always-loaded graph
 * would pull `livekit-client` into the initial bundle and blow plan.md's 300 KB budget for every
 * employee on every page load — including everyone who never starts a meeting. `vite.config.ts`
 * asserts the split held.
 */

import { useCallback, useEffect, useState } from 'react'

import type { TrackReference } from '@livekit/components-react'
import {
  isTrackReference,
  LiveKitRoom,
  RoomAudioRenderer,
  useLocalParticipant,
  useTracks,
  VideoTrack,
} from '@livekit/components-react'
import { Track } from 'livekit-client'

/** How long before expiry the renewal notice appears. */
const EXPIRY_WARNING_MS = 60_000

/** What the room needs to connect and how to leave it. */
export interface MeetingRoomProps {
  readonly token: string
  readonly serverUrl: string
  readonly onLeave: () => void
  /** Surfaced so the caller can renew before the token expires rather than dropping the call. */
  readonly expiresAt?: string | undefined
}

/** The meeting room. */
export function MeetingRoom({ token, serverUrl, onLeave, expiresAt }: MeetingRoomProps) {
  const [failed, setFailed] = useState<string | null>(null)

  // The token is short-lived by design (five minutes, matching FR-030's revocation bound). A
  // client that does not notice will simply be disconnected mid-sentence; warning lets the caller
  // renew. Not a failure state — the call is still live while this is showing.
  //
  // Computed on a timer rather than during render. "Is it nearly expired" depends on the current
  // time, which is not derivable from props — reading the clock while rendering makes the result
  // change on any unrelated re-render and never change when nothing re-renders.
  const [expiringSoon, setExpiringSoon] = useState(false)

  useEffect(() => {
    if (expiresAt === undefined) {
      return
    }

    const deadline = new Date(expiresAt).getTime()

    const check = () => {
      setExpiringSoon(deadline - Date.now() < EXPIRY_WARNING_MS)
    }

    check()

    const timer = setInterval(check, 10_000)

    return () => {
      clearInterval(timer)
    }
  }, [expiresAt])

  const onError = useCallback((error: Error) => {
    setFailed(error.message)
  }, [])

  if (failed !== null) {
    return (
      <div className="meeting-room__failed" role="alert">
        <p>The meeting could not be joined: {failed}</p>
        <button type="button" onClick={onLeave}>
          Close
        </button>
      </div>
    )
  }

  return (
    <LiveKitRoom
      token={token}
      serverUrl={serverUrl}
      connect
      // Both on by default: FR-042 asks for two-way audio and video for all participants, and a
      // room people join muted is a room where the first minute is spent saying "you're on mute".
      audio
      video
      onDisconnected={onLeave}
      onError={onError}
      data-testid="meeting-room"
    >
      {expiringSoon && (
        <p className="meeting-room__expiring" role="status">
          Reconnecting shortly to keep this meeting open.
        </p>
      )}

      <ParticipantGrid />
      <MeetingControls onLeave={onLeave} />

      {/*
        Renders every remote audio track. Without it participants see each other and hear nothing —
        a failure that looks like a network problem and is a missing element.
      */}
      <RoomAudioRenderer />
    </LiveKitRoom>
  )
}

/** Every participant's video, plus whatever is being shared (FR-042, FR-048). */
function ParticipantGrid() {
  const tracks = useTracks(
    [
      { source: Track.Source.Camera, withPlaceholder: true },

      // Screen share is a track like any other. Requested explicitly so a shared screen appears
      // for everyone without a second code path, which is what keeps FR-050's takeover rule
      // working by itself: the SFU replaces the published track and every tile follows.
      { source: Track.Source.ScreenShare, withPlaceholder: false },
    ],
    { onlySubscribed: false },
  )

  // `withPlaceholder: true` on the camera source means this list also holds entries for
  // participants who have published nothing yet — that is what puts a tile on screen for someone
  // who joined with their camera off, rather than leaving a gap where they should be. Those
  // placeholders carry no publication, so `VideoTrack` cannot render them; `isTrackReference`
  // narrows to the ones that can. Without the narrowing the tile list is typed as something
  // `VideoTrack` refuses, and forcing it through would put a placeholder into a component that
  // dereferences the publication.
  const from = (source: Track.Source) => (track: unknown): track is TrackReference =>
    isTrackReference(track) && track.source === source

  const shared = tracks.filter(from(Track.Source.ScreenShare))
  const cameras = tracks.filter(from(Track.Source.Camera))

  return (
    <div className="meeting-room__grid" data-testid="participant-grid">
      {shared.length > 0 && (
        // Shared content takes the stage. FR-049 requires it legible enough to read document text,
        // which mostly means giving it the space rather than tiling it with the faces.
        <div className="meeting-room__stage" data-testid="screen-share">
          {shared.map((track) => (
            <VideoTrack key={track.participant.identity + track.source} trackRef={track} />
          ))}
        </div>
      )}

      <ul className="meeting-room__tiles">
        {cameras.map((track) => (
          <li key={track.participant.identity} className="meeting-room__tile">
            <VideoTrack trackRef={track} />
            <span className="meeting-room__name">{track.participant.name}</span>
          </li>
        ))}
      </ul>
    </div>
  )
}

/** Mute, camera, screen share, and leave (FR-045, FR-048). */
function MeetingControls({ onLeave }: { readonly onLeave: () => void }) {
  const { localParticipant, isMicrophoneEnabled, isCameraEnabled, isScreenShareEnabled } =
    useLocalParticipant()

  // No local mirror of isScreenShareEnabled. The SDK value IS the state — copying it into
  // useState and re-syncing in an effect gives two sources of truth for one fact, and the copy is
  // stale for a frame every time sharing starts or stops.
  const toggleMic = useCallback(() => {
    void localParticipant.setMicrophoneEnabled(!isMicrophoneEnabled)
  }, [localParticipant, isMicrophoneEnabled])

  const toggleCamera = useCallback(() => {
    void localParticipant.setCameraEnabled(!isCameraEnabled)
  }, [localParticipant, isCameraEnabled])

  const toggleShare = useCallback(() => {
    // The browser's own picker is what offers "entire screen" versus "a window" (FR-048), and it
    // is the only thing that can: the page cannot enumerate windows, and a single-window capture
    // never receives frames from anything else. FR-048's "MUST NOT reveal any other application"
    // is therefore enforced by the platform rather than by this code — which is the strongest
    // place for it to live, and worth stating because it looks like an omission.
    void localParticipant.setScreenShareEnabled(!isScreenShareEnabled, { audio: false })
  }, [localParticipant, isScreenShareEnabled])

  return (
    <div className="meeting-room__controls">
      <button
        type="button"
        onClick={toggleMic}
        aria-pressed={isMicrophoneEnabled}
        data-testid="toggle-microphone"
      >
        {isMicrophoneEnabled ? 'Mute' : 'Unmute'}
      </button>

      <button
        type="button"
        onClick={toggleCamera}
        aria-pressed={isCameraEnabled}
        data-testid="toggle-camera"
      >
        {isCameraEnabled ? 'Stop video' : 'Start video'}
      </button>

      <button
        type="button"
        onClick={toggleShare}
        aria-pressed={isScreenShareEnabled}
        data-testid="toggle-screen-share"
      >
        {isScreenShareEnabled ? 'Stop sharing' : 'Share screen'}
      </button>

      <button type="button" onClick={onLeave} data-testid="leave-meeting">
        Leave
      </button>
    </div>
  )
}
