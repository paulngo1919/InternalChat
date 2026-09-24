import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { ConversationView } from '../src/features/messages/ConversationView'
import { aMessage, fakeMessagingClient, renderWithProviders } from './helpers'

/**
 * Where the pieces meet (history, real-time, the offline queue, read state).
 *
 * <b>The reconciliation rule is why this file exists.</b> A message is identified by
 * `clientMessageKey` while it is pending and by `id` once confirmed, and the two have to resolve to
 * one bubble. Getting it wrong shows every message the reader sent twice — once optimistically,
 * once for real — and it only shows up when the live event beats the HTTP response, which is most
 * of the time on a fast connection and never on a developer's machine with a local server.
 *
 * `react-virtuoso` is stubbed for the same reason as in `message-list.test.tsx`: jsdom has no
 * layout, so a real one measures a zero-height viewport and renders nothing at all.
 */

vi.mock('react-virtuoso', () => ({
  Virtuoso: (props: {
    data: { key: string }[]
    itemContent: (index: number, row: unknown) => React.ReactNode
    computeItemKey: (index: number, row: unknown) => string
    startReached: () => void
  }) => (
    <div data-testid="virtuoso">
      <button
        type="button"
        data-testid="reach-top"
        onClick={() => {
          props.startReached()
        }}
      />
      {props.data.map((row, index) => (
        <div key={props.computeItemKey(index, row)}>{props.itemContent(index, row)}</div>
      ))}
    </div>
  ),
}))

afterEach(cleanup)

function renderView(overrides: Partial<Parameters<typeof ConversationView>[0]> = {}) {
  const client = overrides.client ?? fakeMessagingClient()

  const result = renderWithProviders(
    <ConversationView
      conversationId="c1"
      currentEmployeeId="e1"
      client={client}
      connected
      typing={[]}
      names={{}}
      incoming={[]}
      kind="group"
      historyVisibility="from_join"
      mutedUntil={null}
      {...overrides}
    />,
  )

  return { ...result, client }
}

describe('history', () => {
  it('loads the conversation history on mount', async () => {
    const client = fakeMessagingClient({
      getHistory: vi.fn(() =>
        Promise.resolve({
          items: [aMessage({ id: 'm1', seq: 1, body: 'earlier' })],
          nextCursor: null,
          hasMore: false,
        }),
      ),
    })

    renderView({ client })

    expect(await screen.findByText('earlier')).toBeInTheDocument()
    expect(client.getHistory).toHaveBeenCalledWith('c1')
  })

  it('asks for an older page keyed on the lowest sequence it holds', async () => {
    const getHistory = vi
      .fn()
      .mockResolvedValueOnce({
        items: [aMessage({ id: 'm5', seq: 5 }), aMessage({ id: 'm6', seq: 6 })],
        nextCursor: null,
        hasMore: true,
      })
      .mockResolvedValueOnce({
        items: [aMessage({ id: 'm4', seq: 4, body: 'older' })],
        nextCursor: null,
        hasMore: false,
      })

    renderView({ client: fakeMessagingClient({ getHistory }) })

    await screen.findByTestId('virtuoso')
    await waitFor(() => {
      expect(getHistory).toHaveBeenCalledTimes(1)
    })

    fireEvent.click(screen.getByTestId('reach-top'))

    // Keyset, not offset. The lowest sequence already held is the only stable anchor in a list
    // something is always being inserted into.
    await waitFor(() => {
      expect(getHistory).toHaveBeenLastCalledWith('c1', 5)
    })

    expect(await screen.findByText('older')).toBeInTheDocument()
  })

  it('reloads from scratch when the conversation changes', async () => {
    const getHistory = vi
      .fn()
      .mockResolvedValueOnce({
        items: [aMessage({ id: 'm1', body: 'in c1' })],
        nextCursor: null,
        hasMore: false,
      })
      .mockResolvedValueOnce({
        items: [aMessage({ id: 'm2', conversationId: 'c2', body: 'in c2' })],
        nextCursor: null,
        hasMore: false,
      })

    const client = fakeMessagingClient({ getHistory })
    const { rerender } = renderView({ client })

    expect(await screen.findByText('in c1')).toBeInTheDocument()

    rerender(
      <ConversationView
        conversationId="c2"
        currentEmployeeId="e1"
        client={client}
        connected
        typing={[]}
        names={{}}
        incoming={[]}
        kind="group"
        historyVisibility="from_join"
        mutedUntil={null}
      />,
    )

    // The previous conversation's messages must not linger. Seeing another conversation's
    // transcript under this one's header is the kind of bug people screenshot.
    expect(await screen.findByText('in c2')).toBeInTheDocument()
    expect(screen.queryByText('in c1')).not.toBeInTheDocument()
  })
})

describe('applying messages', () => {
  it('absorbs the same message arriving twice', async () => {
    const duplicate = aMessage({ id: 'm1', seq: 1, body: 'only once' })

    renderView({
      client: fakeMessagingClient({
        getHistory: vi.fn(() =>
          Promise.resolve({ items: [duplicate], nextCursor: null, hasMore: false }),
        ),
      }),
      incoming: [duplicate],
    })

    // Delivery is at-least-once and Resync deliberately re-sends what a client may already hold, so
    // this is the ordinary case rather than an edge one (contracts/signalr-hub.md).
    await waitFor(() => {
      expect(screen.getAllByText('only once')).toHaveLength(1)
    })
  })

  it('ignores a message addressed to a different conversation', async () => {
    renderView({
      incoming: [aMessage({ id: 'm9', conversationId: 'other', body: 'not for here' })],
    })

    await screen.findByTestId('virtuoso')

    expect(screen.queryByText('not for here')).not.toBeInTheDocument()
  })

  it('orders the transcript by sequence regardless of which source delivered it', async () => {
    renderView({
      client: fakeMessagingClient({
        getHistory: vi.fn(() =>
          Promise.resolve({
            items: [aMessage({ id: 'm1', seq: 1, body: 'first' })],
            nextCursor: null,
            hasMore: false,
          }),
        ),
      }),
      // Arrives later but belongs between — exactly what Resync produces after a reconnect.
      incoming: [
        aMessage({ id: 'm3', seq: 3, body: 'third' }),
        aMessage({ id: 'm2', seq: 2, body: 'second' }),
      ],
    })

    await screen.findByText('first')

    const text = screen.getByTestId('virtuoso').textContent

    expect(text.indexOf('first')).toBeLessThan(text.indexOf('second'))
    expect(text.indexOf('second')).toBeLessThan(text.indexOf('third'))
  })
})

describe('sending', () => {
  it('reconciles an optimistic bubble with the confirmed message rather than showing both', async () => {
    const client = fakeMessagingClient()

    renderView({ client })

    await screen.findByTestId('virtuoso')

    fireEvent.change(screen.getByTestId('composer'), {
      target: { value: 'hello', selectionStart: 5 },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    await waitFor(() => {
      expect(client.sendMessage).toHaveBeenCalled()
    })

    // One bubble, not two. The optimistic row is keyed by clientMessageKey and the confirmed one by
    // id; without the reconciliation the reader sees every message they send duplicated.
    await waitFor(() => {
      expect(screen.queryByTestId('pending-message')).not.toBeInTheDocument()
    })
  })

  it('keeps retrying a send that failed for a transient reason', async () => {
    const sendMessage = vi.fn(() => Promise.reject(Object.assign(new Error('offline'), { status: 0 })))

    renderView({ client: fakeMessagingClient({ sendMessage }) })

    await screen.findByTestId('virtuoso')

    fireEvent.change(screen.getByTestId('composer'), {
      target: { value: 'hello', selectionStart: 5 },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    // Stays pending and stays visible. FR-018: the queue holds it and delivers it on reconnect.
    expect(await screen.findByTestId('pending-message')).toBeInTheDocument()
  })

  it('drops a send the server refused permanently', async () => {
    const sendMessage = vi.fn(() =>
      Promise.reject(Object.assign(new Error('refused'), { status: 403 })),
    )

    renderView({ client: fakeMessagingClient({ sendMessage }) })

    await screen.findByTestId('virtuoso')

    fireEvent.change(screen.getByTestId('composer'), {
      target: { value: 'hello', selectionStart: 5 },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    // A 403 will not succeed on retry — a member who was removed stays removed. Leaving it queued
    // would block every message behind it forever.
    await waitFor(() => {
      expect(screen.queryByTestId('pending-message')).not.toBeInTheDocument()
    })
  })

  it('retries a rate limit rather than discarding the message', async () => {
    const sendMessage = vi.fn(() =>
      Promise.reject(Object.assign(new Error('slow down'), { status: 429 })),
    )

    renderView({ client: fakeMessagingClient({ sendMessage }) })

    await screen.findByTestId('virtuoso')

    fireEvent.change(screen.getByTestId('composer'), {
      target: { value: 'hello', selectionStart: 5 },
    })
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    // 429 is the one 4xx worth retrying. Treating it as permanent would throw away a message for
    // the crime of being typed quickly.
    expect(await screen.findByTestId('pending-message')).toBeInTheDocument()
  })
})

describe('read state', () => {
  it('advances the read position to the newest visible message', async () => {
    const client = fakeMessagingClient({
      getHistory: vi.fn(() =>
        Promise.resolve({
          items: [aMessage({ id: 'm1', seq: 1 }), aMessage({ id: 'm2', seq: 7 })],
          nextCursor: null,
          hasMore: false,
        }),
      ),
    })

    renderView({ client })

    // FR-036. Clears the badge on this employee's other devices without them doing anything beyond
    // looking at the screen they already have open.
    await waitFor(() => {
      expect(client.markRead).toHaveBeenCalledWith('c1', 7)
    })
  })

  it('marks nothing for an empty conversation', async () => {
    const client = fakeMessagingClient()

    renderView({ client })

    await screen.findByTestId('virtuoso')

    // `seq` 0 would be a real claim about having read something.
    expect(client.markRead).not.toHaveBeenCalled()
  })
})

describe('mute', () => {
  it('reflects the incoming mute state and toggles it optimistically', async () => {
    const client = fakeMessagingClient()

    renderView({ client, mutedUntil: null })

    const toggle = await screen.findByTestId('mute-toggle')

    expect(toggle).toHaveAttribute('aria-pressed', 'false')
    expect(toggle).toHaveTextContent('Mute')

    fireEvent.click(toggle)

    expect(toggle).toHaveAttribute('aria-pressed', 'true')
    expect(client.muteConversation).toHaveBeenCalledWith('c1', expect.any(String) as unknown)
  })

  it('unmutes with an explicit null rather than an absent value', async () => {
    const client = fakeMessagingClient()

    renderView({ client, mutedUntil: '2036-01-01T00:00:00Z' })

    const toggle = await screen.findByTestId('mute-toggle')

    expect(toggle).toHaveTextContent('Unmute')

    fireEvent.click(toggle)

    expect(client.muteConversation).toHaveBeenCalledWith('c1', null)
  })

  it('resyncs the toggle when the conversation changes', async () => {
    const client = fakeMessagingClient()

    const { rerender } = renderView({ client, conversationId: 'c1', mutedUntil: null })

    expect(await screen.findByTestId('mute-toggle')).toHaveTextContent('Mute')

    rerender(
      <ConversationView
        conversationId="c2"
        currentEmployeeId="e1"
        client={client}
        connected
        typing={[]}
        names={{}}
        incoming={[]}
        kind="group"
        historyVisibility="from_join"
        mutedUntil="2036-01-01T00:00:00Z"
      />,
    )

    // This component is not remounted when the id changes, so a useState initialiser alone would
    // only ever apply to whichever conversation happened to be first — leaving the toggle showing
    // the previous conversation's mute state.
    expect(await screen.findByTestId('mute-toggle')).toHaveTextContent('Unmute')
  })
})

describe('group-only affordances', () => {
  it('shows the history rule and member list for a group', async () => {
    renderView({ kind: 'group' })

    expect(await screen.findByTestId('history-notice')).toBeInTheDocument()
  })

  it('shows neither for a direct conversation', async () => {
    const client = fakeMessagingClient()

    renderView({ client, kind: 'direct' })

    await screen.findByTestId('virtuoso')

    // A direct conversation has no history rule to state and no membership to manage, and fetching
    // members for one would be a request whose answer nothing renders.
    expect(screen.queryByTestId('history-notice')).not.toBeInTheDocument()
    expect(client.listMembers).not.toHaveBeenCalled()
  })

  it('offers the conversation members as mention candidates, excluding the reader', async () => {
    const client = fakeMessagingClient({
      listMembers: vi.fn(() =>
        Promise.resolve([
          {
            employee: {
              id: 'e1',
              displayName: 'An Nguyen',
              email: 'an@x.test',
              avatarUrl: null,
              status: 'active' as const,
            },
            role: 'admin' as const,
            joinedAt: '2026-01-01T00:00:00Z',
          },
          {
            employee: {
              id: 'e2',
              displayName: 'Bình Tran',
              email: 'binh@x.test',
              avatarUrl: null,
              status: 'active' as const,
            },
            role: 'member' as const,
            joinedAt: '2026-01-01T00:00:00Z',
          },
        ]),
      ),
    })

    renderView({ client })

    await screen.findByTestId('composer')

    fireEvent.change(screen.getByTestId('composer'), {
      target: { value: 'hi @', selectionStart: 4 },
    })

    const options = await screen.findAllByTestId('mention-option')

    // FR-015 resolves mentions against active members. Mentioning yourself would notify you about
    // your own message.
    expect(options.map((option) => option.textContent)).toEqual(['Bình Tran'])
  })
})

describe('connection', () => {
  it('tells the composer the transport is down', async () => {
    renderView({ connected: false })

    expect(await screen.findByTestId('offline-notice')).toBeInTheDocument()
  })

  it('shows who is typing', async () => {
    renderView({ typing: ['e2'], names: { e2: 'Bình' } })

    expect(await screen.findByTestId('typing-indicator')).toHaveTextContent('Bình is typing…')
  })
})
