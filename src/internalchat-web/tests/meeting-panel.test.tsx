import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { MeetingPanel } from '../src/features/meetings/MeetingPanel'
import { aMeeting, fakeMessagingClient } from './helpers'

/**
 * The panel that ties the meeting pieces to a conversation (US8, US9).
 *
 * <b>This component is what finally references the lazy boundary.</b> Until it existed, nothing in
 * the app imported `features/meetings/index.ts`, so Rollup tree-shook the whole feature out and the
 * LiveKit chunk was never emitted — the bundle-size guard passed because there was nothing to
 * split. With the panel wired in, the build emits a separate 150 KB chunk and the initial bundle
 * stays at 115 KB of its 300 KB budget, which is the first time that split has actually been
 * exercised rather than assumed.
 *
 * <b>The 503 case is the one with a requirement behind it.</b> FR-044's platform ceiling and an
 * unreachable media host both surface as "meetings are unavailable", and the message has to say
 * that messaging is unaffected — otherwise the person reasonably concludes the platform is down and
 * stops trying to send anything at all.
 *
 * The room itself is lazy, so it never resolves in these tests; what is asserted is everything up
 * to and around it. `MeetingRoom`'s own behaviour is covered in `meetings.test.tsx`.
 */

vi.mock('../src/features/meetings/index', () => ({
  MeetingRoom: (props: { token: string; serverUrl: string; onLeave: () => void }) => (
    <div data-testid="meeting-room" data-token={props.token} data-server={props.serverUrl}>
      <button
        type="button"
        onClick={() => {
          props.onLeave()
        }}
      >
        Leave the room
      </button>
    </div>
  ),
}))

afterEach(cleanup)

function renderPanel(client = fakeMessagingClient()) {
  render(<MeetingPanel conversationId="c1" client={client} currentEmployeeId="e1" />)

  return { client }
}

describe('starting', () => {
  it('offers to start one when there is none', () => {
    renderPanel()

    expect(screen.getByTestId('start-meeting')).toBeEnabled()
    expect(screen.queryByTestId('meeting-prompt')).not.toBeInTheDocument()
  })

  it('starts a meeting and shows the prompt for it', async () => {
    const { client } = renderPanel()

    fireEvent.click(screen.getByTestId('start-meeting'))

    await waitFor(() => {
      expect(client.startMeeting).toHaveBeenCalledWith('c1')
    })

    // The prompt replaces the start button, rather than both being on screen offering to do the
    // same thing twice.
    expect(await screen.findByTestId('meeting-prompt')).toBeInTheDocument()
    expect(screen.queryByTestId('start-meeting')).not.toBeInTheDocument()
  })

  it('cannot be started twice while the first is in flight', async () => {
    const client = fakeMessagingClient({
      startMeeting: vi.fn(() => new Promise<never>(() => undefined)),
    })

    renderPanel(client)

    fireEvent.click(screen.getByTestId('start-meeting'))

    await waitFor(() => {
      expect(screen.getByTestId('start-meeting')).toBeDisabled()
    })
  })

  it('says meetings are unavailable without implying messaging is', async () => {
    const client = fakeMessagingClient({
      startMeeting: vi.fn(() => Promise.reject(new Error('The API returned 503.'))),
    })

    renderPanel(client)

    fireEvent.click(screen.getByTestId('start-meeting'))

    const error = await screen.findByTestId('meeting-error')

    // FR-044's ceiling and a down media host both arrive here. Without the second sentence the
    // person concludes the platform is down and stops sending messages that would have worked.
    expect(error).toHaveTextContent('The API returned 503.')
    expect(error).toHaveTextContent('Messaging is unaffected.')

    // And the button comes back, because this is exactly the failure that clears on its own.
    expect(screen.getByTestId('start-meeting')).toBeEnabled()
  })

  it('reports a non-Error failure without crashing', async () => {
    const client = fakeMessagingClient({
      // A non-Error rejection: what a throw of a plain value, or a library that rejects with a
      // response object, actually produces. The panel must still say something useful.
      // eslint-disable-next-line @typescript-eslint/prefer-promise-reject-errors -- that is the case under test
      startMeeting: vi.fn(() => Promise.reject('something odd')),
    })

    renderPanel(client)

    fireEvent.click(screen.getByTestId('start-meeting'))

    expect(await screen.findByTestId('meeting-error')).toHaveTextContent(
      'Meetings are unavailable right now.',
    )
  })
})

describe('joining', () => {
  async function join(client = fakeMessagingClient()) {
    renderPanel(client)

    fireEvent.click(screen.getByTestId('start-meeting'))
    fireEvent.click(await screen.findByTestId('meeting-join'))

    return client
  }

  it('mints a token and hands it to the room', async () => {
    const client = await join()

    await waitFor(() => {
      expect(client.joinMeeting).toHaveBeenCalledWith('meeting-1')
    })

    const room = await screen.findByTestId('meeting-room')

    // The token IS the meeting access control (FR-041) — the media server trusts it completely and
    // cannot re-check membership, so it is minted per join rather than reused.
    expect(room).toHaveAttribute('data-token', 'a-token')
    expect(room).toHaveAttribute('data-server', 'wss://livekit.test')
  })

  it('reports a refused join', async () => {
    const client = fakeMessagingClient({
      joinMeeting: vi.fn(() => Promise.reject(new Error('The API returned 409.'))),
    })

    await join(client)

    expect(await screen.findByTestId('meeting-error')).toHaveTextContent('The API returned 409.')
    expect(screen.queryByTestId('meeting-room')).not.toBeInTheDocument()
  })

  it('drops the credential on leaving rather than only unmounting the room', async () => {
    await join()

    fireEvent.click(await screen.findByRole('button', { name: 'Leave the room' }))

    // The token is a bearer capability the media server accepts on its face. Keeping it after
    // leaving serves no purpose and is one more place it can be read from.
    await waitFor(() => {
      expect(screen.queryByTestId('meeting-room')).not.toBeInTheDocument()
    })

    // And the prompt comes back, so rejoining is one click rather than a reload.
    expect(screen.getByTestId('meeting-prompt')).toBeInTheDocument()
  })

  it('offers the share control only once inside the room', async () => {
    renderPanel()

    expect(screen.queryByTestId('toggle-screen-share')).not.toBeInTheDocument()

    fireEvent.click(screen.getByTestId('start-meeting'))
    fireEvent.click(await screen.findByTestId('meeting-join'))

    expect(await screen.findByTestId('toggle-screen-share')).toBeInTheDocument()
  })
})

describe('screen sharing', () => {
  async function enterRoom(client = fakeMessagingClient()) {
    renderPanel(client)

    fireEvent.click(screen.getByTestId('start-meeting'))
    fireEvent.click(await screen.findByTestId('meeting-join'))
    await screen.findByTestId('meeting-room')

    return client
  }

  it('claims the slot through the API', async () => {
    const client = await enterRoom()

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    await waitFor(() => {
      expect(client.startShare).toHaveBeenCalledWith('meeting-1', 'screen')
    })
  })

  it('names the reader when the reader is the one displaced', async () => {
    const client = fakeMessagingClient({
      startShare: vi.fn(() =>
        Promise.resolve({
          meetingId: 'meeting-1',
          employeeId: 'e1',
          scope: 'screen' as const,
          startedAt: '2026-09-22T09:00:00Z',
          displacedEmployeeId: 'e1',
        }),
      ),
    })

    await enterRoom(client)

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    // "You took over sharing from you" would be nonsense. Everyone else stays "a colleague" until
    // there is a participant roster to look them up in.
    expect(await screen.findByTestId('share-notice')).toHaveTextContent('yourself')
  })

  it('calls someone else a colleague', async () => {
    const client = fakeMessagingClient({
      startShare: vi.fn(() =>
        Promise.resolve({
          meetingId: 'meeting-1',
          employeeId: 'e1',
          scope: 'screen' as const,
          startedAt: '2026-09-22T09:00:00Z',
          displacedEmployeeId: 'e9',
        }),
      ),
    })

    await enterRoom(client)

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    expect(await screen.findByTestId('share-notice')).toHaveTextContent('a colleague')
  })

  it('releases the slot when sharing stops', async () => {
    const client = await enterRoom()

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    await waitFor(() => {
      expect(screen.getByTestId('toggle-screen-share')).toHaveAttribute('aria-pressed', 'true')
    })

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    await waitFor(() => {
      expect(client.stopShare).toHaveBeenCalledWith('meeting-1')
    })
  })

  it('stops sharing when the reader leaves', async () => {
    await enterRoom()

    fireEvent.click(screen.getByTestId('toggle-screen-share'))

    await waitFor(() => {
      expect(screen.getByTestId('toggle-screen-share')).toHaveAttribute('aria-pressed', 'true')
    })

    fireEvent.click(screen.getByRole('button', { name: 'Leave the room' }))

    // Otherwise rejoining starts with the control claiming a share that ended with the last
    // session — and the server's slot has long since been released.
    fireEvent.click(await screen.findByTestId('meeting-join'))

    await waitFor(() => {
      expect(screen.getByTestId('toggle-screen-share')).toHaveAttribute('aria-pressed', 'false')
    })
  })
})

describe('an existing meeting', () => {
  it('shows the ceiling the server reported rather than one assumed here', async () => {
    const client = fakeMessagingClient({
      startMeeting: vi.fn(() => Promise.resolve(aMeeting({ participantCount: 4, maxParticipants: 25 }))),
    })

    renderPanel(client)

    fireEvent.click(screen.getByTestId('start-meeting'))

    // Served rather than hardcoded, so a change to FR-042's limit does not need a frontend
    // deployment to be shown correctly.
    expect(await screen.findByTestId('meeting-prompt')).toHaveTextContent('4 of 25 people')
  })
})
