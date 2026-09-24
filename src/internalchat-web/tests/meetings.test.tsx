import { act, cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { JoinPrompt, type Meeting } from '../src/features/meetings/JoinPrompt'
import {
  ScreenShareControl,
  ShareDisplacedNotice,
  type ScreenShareApi,
} from '../src/features/meetings/ScreenShareControl'
import { SharedView } from '../src/features/meetings/SharedView'
import { useLiveMeeting } from '../src/features/meetings/useLiveMeeting'
import { pending } from './at'

/**
 * Meetings and screen sharing (US8 — FR-041 to FR-051).
 *
 * <b>The LiveKit SDK is stubbed, and that is not only for jsdom's benefit.</b> `MeetingRoom` is the
 * single module allowed to import it: `index.ts` reaches it through a dynamic `import()`, which is
 * what makes Rollup emit it as its own chunk, and `vite.config.ts` fails the build if a static
 * import ever pulls it into the initial bundle. A test that imported the real SDK statically would
 * not break that guard — it only inspects the build — but it would make this suite depend on a
 * multi-megabyte package for behaviour that is entirely ours.
 *
 * <b>What is actually asserted is the half the SFU cannot do.</b> FR-048's "sharing one window MUST
 * NOT reveal any other application" is enforced by the browser's own picker, not by this code, and
 * no test here can or should claim otherwise. What is ours is the ordering (capture before claim),
 * the takeover notice on both sides, and the attribution that makes a handover distinguishable from
 * the presenter switching applications.
 */

const sdk = {
  microphoneEnabled: true,
  cameraEnabled: true,
  screenShareEnabled: false,
  setMicrophoneEnabled: vi.fn(() => Promise.resolve()),
  setCameraEnabled: vi.fn(() => Promise.resolve()),
  setScreenShareEnabled: vi.fn(() => Promise.resolve()),
  tracks: [] as { source: string; participant: { identity: string; name: string } }[],
  roomProps: null as Record<string, unknown> | null,
}

vi.mock('livekit-client', () => ({
  Track: { Source: { Camera: 'camera', ScreenShare: 'screen_share' } },
}))

vi.mock('@livekit/components-react', () => ({
  // MeetingRoom narrows away the camera-source placeholders before rendering, so the mock has
  // to answer this. Every track these tests supply is a real one.
  isTrackReference: () => true,
  LiveKitRoom: (props: Record<string, unknown> & { children?: React.ReactNode }) => {
    sdk.roomProps = props

    return <div data-testid="livekit-room">{props.children}</div>
  },
  RoomAudioRenderer: () => <div data-testid="room-audio" />,
  VideoTrack: (props: { trackRef: { participant: { identity: string } } }) => (
    <div data-testid="video-track" data-identity={props.trackRef.participant.identity} />
  ),
  useTracks: () => sdk.tracks,
  useLocalParticipant: () => ({
    localParticipant: {
      setMicrophoneEnabled: sdk.setMicrophoneEnabled,
      setCameraEnabled: sdk.setCameraEnabled,
      setScreenShareEnabled: sdk.setScreenShareEnabled,
    },
    isMicrophoneEnabled: sdk.microphoneEnabled,
    isCameraEnabled: sdk.cameraEnabled,
    isScreenShareEnabled: sdk.screenShareEnabled,
  }),
}))

const { MeetingRoom } = await import('../src/features/meetings/MeetingRoom')

function aMeeting(overrides: Partial<Meeting> = {}): Meeting {
  return {
    id: 'meeting-1',
    conversationId: 'c1',
    startedBy: 'e2',
    startedAt: '2026-09-22T09:00:00Z',
    endedAt: null,
    participantCount: 3,
    maxParticipants: 25,
    ...overrides,
  }
}

beforeEach(() => {
  sdk.microphoneEnabled = true
  sdk.cameraEnabled = true
  sdk.screenShareEnabled = false
  sdk.tracks = []
  sdk.roomProps = null
  vi.clearAllMocks()
})

afterEach(cleanup)

describe('JoinPrompt', () => {
  it('shows nothing when there is no meeting', () => {
    const { container } = render(<JoinPrompt meeting={null} onJoin={() => undefined} />)

    expect(container).toBeEmptyDOMElement()
  })

  it('shows nothing once the meeting has ended', () => {
    render(
      <JoinPrompt
        meeting={aMeeting({ endedAt: '2026-09-22T09:30:00Z' })}
        onJoin={() => undefined}
      />,
    )

    // Derived from the prop rather than stored. Copying it into state would need an effect to
    // re-sync and could disagree — leaving a "join" button for a meeting nobody is in.
    expect(screen.queryByTestId('meeting-prompt')).not.toBeInTheDocument()
  })

  it('states how many people are in it, with the right plural', () => {
    render(<JoinPrompt meeting={aMeeting({ participantCount: 1 })} onJoin={() => undefined} />)

    expect(screen.getByTestId('meeting-prompt')).toHaveTextContent('1 of 25 person')

    cleanup()
    render(<JoinPrompt meeting={aMeeting({ participantCount: 3 })} onJoin={() => undefined} />)

    expect(screen.getByTestId('meeting-prompt')).toHaveTextContent('3 of 25 people')
  })

  it('joins the meeting it is showing', () => {
    const onJoin = vi.fn()

    render(<JoinPrompt meeting={aMeeting()} onJoin={onJoin} />)

    fireEvent.click(screen.getByTestId('meeting-join'))

    expect(onJoin).toHaveBeenCalledWith('meeting-1')
  })

  it('refuses a join once the meeting is at capacity, and says why', () => {
    render(<JoinPrompt meeting={aMeeting({ participantCount: 25 })} onJoin={() => undefined} />)

    const button = screen.getByTestId('meeting-join')

    // FR-042's ceiling. A button that is merely disabled leaves the person wondering whether the
    // page is broken.
    expect(button).toBeDisabled()
    expect(button).toHaveTextContent('Meeting is full')
  })

  it('cannot be pressed twice while a join is in flight', () => {
    render(<JoinPrompt meeting={aMeeting()} onJoin={() => undefined} joining />)

    expect(screen.getByTestId('meeting-join')).toBeDisabled()
  })
})

describe('useLiveMeeting', () => {
  it('subscribes for a conversation and unsubscribes when it goes away', () => {
    const unsubscribe = vi.fn()
    const subscribe = vi.fn(() => unsubscribe)

    const { unmount } = renderHook(() => useLiveMeeting('c1', subscribe))

    expect(subscribe).toHaveBeenCalled()

    unmount()

    // A live subscription per conversation ever opened would accumulate for the life of the tab.
    expect(unsubscribe).toHaveBeenCalled()
  })

  it('does not subscribe without a conversation', () => {
    const subscribe = vi.fn(() => () => undefined)

    renderHook(() => useLiveMeeting(null, subscribe))

    expect(subscribe).not.toHaveBeenCalled()
  })

  it('records a meeting started in this conversation', () => {
    let handlers: Parameters<Parameters<typeof useLiveMeeting>[1]>[0] | null = null

    const { result } = renderHook(() =>
      useLiveMeeting('c1', (h) => {
        handlers = h
        return () => undefined
      }),
    )

    act(() => {
      handlers?.onStarted('meeting-1', 'c1', 'e2')
    })

    expect(result.current?.id).toBe('meeting-1')

    // Optimistic. Showing "0 people" for a meeting somebody just started would read as broken, and
    // the count is corrected the moment the client fetches or joins.
    expect(result.current?.participantCount).toBe(1)
    expect(result.current?.maxParticipants).toBe(25)
  })

  it('ignores a meeting started somewhere else', () => {
    let handlers: Parameters<Parameters<typeof useLiveMeeting>[1]>[0] | null = null

    const { result } = renderHook(() =>
      useLiveMeeting('c1', (h) => {
        handlers = h
        return () => undefined
      }),
    )

    act(() => {
      handlers?.onStarted('meeting-2', 'c9', 'e2')
    })

    // The hub delivers for every conversation this employee belongs to, so events for other
    // conversations arrive here constantly.
    expect(result.current).toBeNull()
  })

  it('clears the meeting when it ends', () => {
    let handlers: Parameters<Parameters<typeof useLiveMeeting>[1]>[0] | null = null

    const { result } = renderHook(() =>
      useLiveMeeting('c1', (h) => {
        handlers = h
        return () => undefined
      }),
    )

    act(() => {
      handlers?.onStarted('meeting-1', 'c1', 'e2')
    })

    act(() => {
      handlers?.onEnded('meeting-1', 'c1')
    })

    // Cleared rather than marked ended: nothing renders an ended meeting, and leaving it in state
    // means the prompt has to keep deciding not to show it.
    expect(result.current).toBeNull()
  })

  it('ignores an end for a different meeting or a different conversation', () => {
    let handlers: Parameters<Parameters<typeof useLiveMeeting>[1]>[0] | null = null

    const { result } = renderHook(() =>
      useLiveMeeting('c1', (h) => {
        handlers = h
        return () => undefined
      }),
    )

    act(() => {
      handlers?.onStarted('meeting-1', 'c1', 'e2')
    })

    act(() => {
      handlers?.onEnded('meeting-other', 'c1')
      handlers?.onEnded('meeting-1', 'c9')
    })

    // A late end for a meeting that has already been replaced would otherwise hide a live one.
    expect(result.current?.id).toBe('meeting-1')
  })
})

describe('MeetingRoom', () => {
  function renderRoom(overrides: Partial<Parameters<typeof MeetingRoom>[0]> = {}) {
    const onLeave = vi.fn()

    render(
      <MeetingRoom
        token="a-token"
        serverUrl="wss://livekit.test"
        onLeave={onLeave}
        {...overrides}
      />,
    )

    return { onLeave }
  }

  it('joins with audio and video on', () => {
    renderRoom()

    // FR-042 asks for two-way audio and video for all participants, and a room people join muted
    // is a room where the first minute is spent saying "you're on mute".
    expect(sdk.roomProps?.audio).toBe(true)
    expect(sdk.roomProps?.video).toBe(true)
    expect(sdk.roomProps?.connect).toBe(true)
    expect(sdk.roomProps?.token).toBe('a-token')
  })

  it('renders remote audio', () => {
    renderRoom()

    // Without this element participants see each other and hear nothing — a failure that looks
    // like a network problem and is a missing component.
    expect(screen.getByTestId('room-audio')).toBeInTheDocument()
  })

  it('leaves when the room disconnects', () => {
    const { onLeave } = renderRoom()

    act(() => {
      ;(sdk.roomProps?.onDisconnected as () => void)()
    })

    expect(onLeave).toHaveBeenCalled()
  })

  it('reports a failed join and offers a way out', () => {
    const { onLeave } = renderRoom()

    act(() => {
      ;(sdk.roomProps?.onError as (error: Error) => void)(new Error('the room is full'))
    })

    expect(screen.getByRole('alert')).toHaveTextContent('the room is full')

    // A dead-end error with no control is a screen someone has to reload out of.
    fireEvent.click(screen.getByRole('button', { name: 'Close' }))

    expect(onLeave).toHaveBeenCalled()
  })

  it('tiles each participant camera with their name', () => {
    sdk.tracks = [
      { source: 'camera', participant: { identity: 'e1', name: 'An Nguyen' } },
      { source: 'camera', participant: { identity: 'e2', name: 'Bình Tran' } },
    ]

    renderRoom()

    expect(screen.getAllByTestId('video-track')).toHaveLength(2)
    expect(screen.getByTestId('participant-grid')).toHaveTextContent('An Nguyen')
  })

  it('gives a shared screen the stage rather than tiling it with the faces', () => {
    sdk.tracks = [
      { source: 'camera', participant: { identity: 'e1', name: 'An Nguyen' } },
      { source: 'screen_share', participant: { identity: 'e2', name: 'Bình Tran' } },
    ]

    renderRoom()

    // FR-049 requires shared content legible enough to read document text, which mostly means
    // giving it the space rather than tiling it alongside five faces.
    expect(screen.getByTestId('screen-share')).toBeInTheDocument()
  })

  it('shows no stage when nobody is sharing', () => {
    sdk.tracks = [{ source: 'camera', participant: { identity: 'e1', name: 'An' } }]

    renderRoom()

    expect(screen.queryByTestId('screen-share')).not.toBeInTheDocument()
  })

  describe('controls', () => {
    it('toggles the microphone to the opposite of the SDK value', () => {
      renderRoom()

      const button = screen.getByTestId('toggle-microphone')

      expect(button).toHaveAttribute('aria-pressed', 'true')
      expect(button).toHaveTextContent('Mute')

      fireEvent.click(button)

      expect(sdk.setMicrophoneEnabled).toHaveBeenCalledWith(false)
    })

    it('toggles the camera', () => {
      sdk.cameraEnabled = false

      renderRoom()

      const button = screen.getByTestId('toggle-camera')

      expect(button).toHaveTextContent('Start video')

      fireEvent.click(button)

      expect(sdk.setCameraEnabled).toHaveBeenCalledWith(true)
    })

    it('toggles screen sharing without capturing its audio', () => {
      renderRoom()

      fireEvent.click(screen.getByTestId('toggle-screen-share'))

      // No audio: sharing a screen should not also broadcast whatever is playing on it, which is
      // the kind of surprise people only discover afterwards.
      expect(sdk.setScreenShareEnabled).toHaveBeenCalledWith(true, { audio: false })
    })

    it('reads the SDK value rather than a local mirror', () => {
      sdk.screenShareEnabled = true

      renderRoom()

      // Copying it into useState and re-syncing in an effect gives two sources of truth for one
      // fact, and the copy is stale for a frame every time sharing starts or stops.
      expect(screen.getByTestId('toggle-screen-share')).toHaveTextContent('Stop sharing')
      expect(screen.getByTestId('toggle-screen-share')).toHaveAttribute('aria-pressed', 'true')
    })

    it('leaves on request', () => {
      const { onLeave } = renderRoom()

      fireEvent.click(screen.getByTestId('leave-meeting'))

      expect(onLeave).toHaveBeenCalled()
    })
  })

  describe('token expiry', () => {
    beforeEach(() => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
    })

    afterEach(() => {
      vi.useRealTimers()
    })

    it('says nothing while the token has plenty of life left', () => {
      renderRoom({ expiresAt: new Date(Date.now() + 5 * 60_000).toISOString() })

      expect(screen.queryByText(/reconnecting shortly/i)).not.toBeInTheDocument()
    })

    it('warns before the token expires rather than dropping the call', async () => {
      renderRoom({ expiresAt: new Date(Date.now() + 70_000).toISOString() })

      await act(async () => {
        vi.advanceTimersByTime(20_000)
      })

      // The token is short-lived by design. A client that does not notice is simply disconnected
      // mid-sentence; this is not a failure state — the call is still live while it shows.
      expect(screen.getByText(/reconnecting shortly/i)).toBeInTheDocument()
    })

    it('runs no timer when there is no expiry to watch', async () => {
      renderRoom()

      await act(async () => {
        vi.advanceTimersByTime(120_000)
      })

      expect(screen.queryByText(/reconnecting shortly/i)).not.toBeInTheDocument()
    })
  })
})

describe('ScreenShareControl', () => {
  function fakeApi(overrides: Partial<ScreenShareApi> = {}): ScreenShareApi {
    return {
      startShare: vi.fn(() => Promise.resolve({ displacedEmployeeId: null })),
      stopShare: vi.fn(() => Promise.resolve()),
      ...overrides,
    }
  }

  function renderControl(overrides: Partial<Parameters<typeof ScreenShareControl>[0]> = {}) {
    const api = overrides.api ?? fakeApi()
    const setCapture = overrides.setCapture ?? vi.fn(() => Promise.resolve())

    render(
      <ScreenShareControl
        meetingId="meeting-1"
        api={api}
        isSharing={false}
        setCapture={setCapture}
        {...overrides}
      />,
    )

    return { api, setCapture }
  }

  it('captures before claiming the slot', async () => {
    const order: string[] = []

    const setCapture = vi.fn(() => {
      order.push('capture')
      return Promise.resolve()
    })

    const api = fakeApi({
      startShare: vi.fn(() => {
        order.push('claim')
        return Promise.resolve({ displacedEmployeeId: null })
      }),
    })

    renderControl({ api, setCapture })

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    await waitFor(() => {
      expect(order).toEqual(['capture', 'claim'])
    })

    // The browser picker can be cancelled. Claiming first would displace whoever is currently
    // sharing on behalf of someone who then changed their mind — a takeover with no share to show.
  })

  it('records the broader scope, which is the safe direction to be wrong in', async () => {
    const { api } = renderControl()

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    await waitFor(() => {
      // getDisplayMedia does not report the chosen scope in any portable way, so 'screen' is what
      // is recorded. Over-claiming in an audit record is safer than under-claiming.
      expect(api.startShare).toHaveBeenCalledWith('meeting-1', 'screen')
    })
  })

  it('names who was displaced, when it can', async () => {
    const api = fakeApi({
      startShare: vi.fn(() => Promise.resolve({ displacedEmployeeId: 'e2' })),
    })

    renderControl({ api, nameOf: (id) => (id === 'e2' ? 'Bình Tran' : 'someone') })

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    expect(await screen.findByTestId('share-notice')).toHaveTextContent(
      'You took over sharing from Bình Tran.',
    )
  })

  it('falls back to a generic name when it cannot resolve one', async () => {
    const api = fakeApi({
      startShare: vi.fn(() => Promise.resolve({ displacedEmployeeId: 'e2' })),
    })

    renderControl({ api })

    expect(screen.getByTestId('toggle-screen-share')).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    expect(await screen.findByTestId('share-notice')).toHaveTextContent('another participant')
  })

  it('says nothing when nobody was displaced', async () => {
    const { api } = renderControl()

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    await waitFor(() => {
      expect(api.startShare).toHaveBeenCalled()
    })

    expect(screen.queryByTestId('share-notice')).not.toBeInTheDocument()
  })

  it('undoes the capture when the claim is refused', async () => {
    const setCapture = vi.fn(() => Promise.resolve())

    const api = fakeApi({
      startShare: vi.fn(() => Promise.reject(new Error('refused'))),
    })

    renderControl({ api, setCapture })

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    expect(await screen.findByTestId('share-notice')).toHaveTextContent(
      'Sharing could not be started.',
    )

    // Otherwise the browser is left publishing a track the server does not know about — invisible
    // to the server's FR-050 slot and very visible to everyone in the meeting.
    expect(setCapture).toHaveBeenCalledWith(false)
  })

  it('survives the undo itself failing', async () => {
    const setCapture = vi
      .fn()
      .mockResolvedValueOnce(undefined)
      .mockRejectedValue(new Error('no track'))

    renderControl({
      api: fakeApi({ startShare: vi.fn(() => Promise.reject(new Error('refused'))) }),
      setCapture,
    })

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    // An unhandled rejection inside a failure path would replace a clear message with a blank
    // screen.
    expect(await screen.findByTestId('share-notice')).toHaveTextContent(
      'Sharing could not be started.',
    )
  })

  it('stops both the capture and the claim', async () => {
    const setCapture = vi.fn(() => Promise.resolve())
    const api = fakeApi()

    renderControl({ api, setCapture, isSharing: true })

    const button = screen.getByTestId('toggle-screen-share')

    expect(button).toHaveTextContent('Stop sharing')
    expect(button).toHaveAttribute('aria-pressed', 'true')

    fireEvent.click(button)

    await waitFor(() => {
      expect(api.stopShare).toHaveBeenCalledWith('meeting-1')
    })

    // Releasing the slot without stopping the capture leaves the browser sharing with no server
    // record; stopping the capture without releasing the slot blocks everyone else.
    expect(setCapture).toHaveBeenCalledWith(false)
  })

  it('cannot be pressed while busy', async () => {
    renderControl({ setCapture: vi.fn(() => pending<undefined>()) })

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    await waitFor(() => {
      expect(screen.getByTestId('toggle-screen-share')).toBeDisabled()
    })
  })

  it('announces a takeover politely', async () => {
    renderControl({
      api: fakeApi({ startShare: vi.fn(() => Promise.resolve({ displacedEmployeeId: 'e2' })) }),
    })

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    const notice = await screen.findByTestId('share-notice')

    // A takeover is information, not an error. Interrupting a screen reader mid-sentence for it
    // would be worse than the delay.
    expect(notice).toHaveAttribute('aria-live', 'polite')
  })
})

describe('ShareDisplacedNotice', () => {
  it('shows nothing when nobody took over', () => {
    const { container } = render(
      <ShareDisplacedNotice displacedBy={null} onDismiss={() => undefined} />,
    )

    expect(container).toBeEmptyDOMElement()
  })

  it('tells the person who lost the slot why their share stopped', () => {
    const onDismiss = vi.fn()

    render(<ShareDisplacedNotice displacedBy="Bình Tran" onDismiss={onDismiss} />)

    // Without this their share simply vanishes, which is indistinguishable from their connection
    // dropping — the other half of FR-050's "visible".
    expect(screen.getByRole('alert')).toHaveTextContent(
      'Bình Tran started sharing, so your screen share stopped.',
    )

    fireEvent.click(screen.getByRole('button', { name: 'Dismiss' }))

    expect(onDismiss).toHaveBeenCalled()
  })
})

describe('SharedView', () => {
  it('renders nothing when nobody is sharing', () => {
    const { container } = render(
      <SharedView presenterName={null}>
        <div />
      </SharedView>,
    )

    // An empty frame would leave a hole in the layout that reads as a broken video.
    expect(container).toBeEmptyDOMElement()
  })

  it('attributes the share to its presenter', () => {
    render(
      <SharedView presenterName="Bình Tran">
        <div data-testid="the-video" />
      </SharedView>,
    )

    // FR-050 from the viewer's side. When sharing changes hands the picture changes and nothing
    // else does — without the name, a handover is indistinguishable from the presenter switching
    // applications.
    expect(screen.getByTestId('share-presenter')).toHaveTextContent(
      "You are viewing Bình Tran's screen",
    )

    expect(screen.getByTestId('the-video')).toBeInTheDocument()
  })

  it('enlarges and collapses', () => {
    render(
      <SharedView presenterName="Bình Tran">
        <div />
      </SharedView>,
    )

    const toggle = screen.getByTestId('toggle-enlarge-share')

    expect(toggle).toHaveTextContent('Enlarge')
    expect(screen.getByTestId('shared-view')).not.toHaveClass('is-enlarged')

    fireEvent.click(toggle)

    // FR-049 is partly a media question and partly a layout one: a 1080p document tiled next to
    // five faces in a 300-pixel column is unreadable no matter how good the stream is.
    expect(screen.getByTestId('shared-view')).toHaveClass('is-enlarged')
    expect(toggle).toHaveTextContent('Exit full view')

    fireEvent.click(toggle)

    expect(screen.getByTestId('shared-view')).not.toHaveClass('is-enlarged')
  })

  it('leaves the enlarged view on Escape', () => {
    render(
      <SharedView presenterName="Bình Tran">
        <div />
      </SharedView>,
    )

    fireEvent.click(screen.getByTestId('toggle-enlarge-share'))
    fireEvent.keyDown(window, { key: 'Escape' })

    expect(screen.getByTestId('shared-view')).not.toHaveClass('is-enlarged')
  })

  it('does not compete for Escape while collapsed', () => {
    const add = vi.spyOn(window, 'addEventListener')

    render(
      <SharedView presenterName="Bình Tran">
        <div />
      </SharedView>,
    )

    // Registered only while enlarged, so it cannot swallow the key from a dialog above it.
    expect(add).not.toHaveBeenCalledWith('keydown', expect.any(Function) as unknown)
  })

  it('ignores other keys while enlarged', () => {
    render(
      <SharedView presenterName="Bình Tran">
        <div />
      </SharedView>,
    )

    fireEvent.click(screen.getByTestId('toggle-enlarge-share'))
    fireEvent.keyDown(window, { key: 'a' })

    expect(screen.getByTestId('shared-view')).toHaveClass('is-enlarged')
  })
})
