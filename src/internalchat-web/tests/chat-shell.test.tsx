import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { ChatConnectionOptions } from '../src/lib/realtime/chatConnection'
import { at } from './at'
import { aConversation, aMessage, renderWithProviders } from './helpers'

/**
 * The messaging screen (`ChatShell`).
 *
 * <b>One connection per tab, and it must survive switching conversations.</b> The hub delivers for
 * every conversation the employee belongs to, so a connection per open conversation would multiply
 * 7,000 concurrent connections by however many tabs people keep open. Rebuilding it on every click
 * would be almost as bad and much harder to notice — it drops and re-establishes the socket, and
 * each re-establishment triggers a Resync.
 *
 * <b>`MembershipRevoked` must clear the open conversation immediately.</b> US3 scenario 3 says
 * access ends at once. Waiting for the list to refetch and quietly drop the row leaves a reader
 * mid-scroll in a conversation they no longer belong to.
 *
 * The connection and the virtualizer are both stubbed: the first because its own behaviour is
 * covered in `chat-connection.test.ts` and what matters here is how the shell wires it, the second
 * because jsdom has no layout.
 */

const hub: { options: ChatConnectionOptions | null; started: number; stopped: number } = {
  options: null,
  started: 0,
  stopped: 0,
}

const typingCalls: { start: string[]; stop: string[] } = { start: [], stop: [] }

vi.mock('../src/lib/realtime/chatConnection', () => ({
  ChatEvents: {},
  ChatConnection: class {
    constructor(options: ChatConnectionOptions) {
      hub.options = options
    }

    start() {
      hub.started += 1
      return Promise.resolve()
    }

    stop() {
      hub.stopped += 1
      return Promise.resolve()
    }

    startTyping(id: string) {
      typingCalls.start.push(id)
      return Promise.resolve()
    }

    stopTyping(id: string) {
      typingCalls.stop.push(id)
      return Promise.resolve()
    }
  },
}))

vi.mock('react-virtuoso', () => ({
  Virtuoso: (props: {
    data: { key: string }[]
    itemContent: (index: number, row: unknown) => React.ReactNode
    computeItemKey: (index: number, row: unknown) => string
  }) => (
    <div data-testid="virtuoso">
      {props.data.map((row, index) => (
        <div key={props.computeItemKey(index, row)}>{props.itemContent(index, row)}</div>
      ))}
    </div>
  ),
}))

const { ChatShell } = await import('../src/features/messages/ChatShell')

/** An authorized fetch that answers each path from a small routing table. */
function authorizedFor(routes: Record<string, unknown>) {
  const calls: string[] = []

  const authorized = vi.fn((path: string) => {
    calls.push(path)

    const key = Object.keys(routes).find((prefix) => path.startsWith(prefix))

    return Promise.resolve({
      ok: true,
      status: 200,
      json: () => Promise.resolve(key === undefined ? {} : routes[key]),
    } as Response)
  })

  return { authorized, calls }
}

function renderShell(routes: Record<string, unknown> = {}) {
  const { authorized, calls } = authorizedFor({
    '/conversations?': { items: [aConversation({ id: 'c1', name: 'Design' })], nextCursor: null },
    '/conversations/c1/messages': { items: [], nextCursor: null, hasMore: false },
    '/conversations/c1/members': [],
    '/conversations/c1/read-state': { conversationId: 'c1', lastReadSeq: 0, unreadCount: 0 },
    '/conversations/c1': aConversation({ id: 'c1', name: 'Design' }),
    ...routes,
  })

  const result = renderWithProviders(
    <ChatShell
      authorized={authorized}
      getAccessToken={() => Promise.resolve('token')}
      currentEmployeeId="e1"
    />,
  )

  return { ...result, authorized, calls }
}

beforeEach(() => {
  hub.options = null
  hub.started = 0
  hub.stopped = 0
  typingCalls.start = []
  typingCalls.stop = []
})

afterEach(cleanup)

describe('the connection', () => {
  it('opens exactly one, and closes it on unmount', () => {
    const { unmount } = renderShell()

    expect(hub.started).toBe(1)

    unmount()

    // A socket left open per unmounted screen is a leak nothing in the UI would show.
    expect(hub.stopped).toBe(1)
  })

  it('survives switching conversations', async () => {
    renderShell()

    fireEvent.click(await screen.findByTestId('conversation-item'))

    await screen.findByTestId('virtuoso')

    // Typing state is stored per conversation precisely so the connection does not have to be
    // rebuilt on a click — rebuilding drops the socket and triggers a Resync each time.
    expect(hub.started).toBe(1)
    expect(hub.stopped).toBe(0)
  })

  it('reports the highest sequence it has applied, per conversation', () => {
    renderShell()

    hub.options?.onMessages([
      aMessage({ id: 'm1', conversationId: 'c1', seq: 3 }),
      aMessage({ id: 'm2', conversationId: 'c1', seq: 9 }),
      aMessage({ id: 'm3', conversationId: 'c2', seq: 2 }),
    ])

    // The gap to close at reconnect. A lower figure asks for messages already held; a higher one
    // silently skips the ones in between.
    expect(hub.options?.lastSeenSeq()).toEqual({ c1: 9, c2: 2 })
  })

  it('never moves a sequence backwards', () => {
    renderShell()

    hub.options?.onMessages([aMessage({ id: 'm1', conversationId: 'c1', seq: 9 })])
    hub.options?.onMessages([aMessage({ id: 'm2', conversationId: 'c1', seq: 4 })])

    // Resync deliberately re-sends what a client may already hold, so an older message arriving
    // after a newer one is the ordinary case — and rewinding would re-request the gap forever.
    expect(hub.options?.lastSeenSeq()).toEqual({ c1: 9 })
  })

  it('hands back a copy, not the live object', () => {
    renderShell()

    hub.options?.onMessages([aMessage({ conversationId: 'c1', seq: 9 })])

    const snapshot = hub.options?.lastSeenSeq() as Record<string, number>

    snapshot.c1 = 999

    expect(hub.options?.lastSeenSeq()).toEqual({ c1: 9 })
  })
})

describe('real-time events', () => {
  it('keeps a message that arrived for a conversation that was not open', async () => {
    renderShell()

    hub.options?.onMessages([
      aMessage({ id: 'm1', conversationId: 'c1', seq: 1, body: 'arrived earlier' }),
    ])

    fireEvent.click(await screen.findByTestId('conversation-item'))

    // Appended rather than replaced, so a message that arrives while a different conversation is
    // open is still there when the reader switches to it.
    expect(await screen.findByText('arrived earlier')).toBeInTheDocument()
  })

  it('shows who is typing, excluding the reader', async () => {
    renderShell()

    fireEvent.click(await screen.findByTestId('conversation-item'))
    await screen.findByTestId('virtuoso')

    hub.options?.onTypingChanged?.({ conversationId: 'c1', employeeIds: ['e1', 'e2'] })

    // Seeing "you are typing…" above your own composer is a bug people screenshot.
    const indicator = await screen.findByTestId('typing-indicator')

    expect(indicator).toHaveTextContent('Someone is typing…')
  })

  it('keeps typing state per conversation', async () => {
    renderShell()

    hub.options?.onTypingChanged?.({ conversationId: 'c2', employeeIds: ['e2'] })

    fireEvent.click(await screen.findByTestId('conversation-item'))
    await screen.findByTestId('virtuoso')

    // The indicator belongs to the conversation, not to the screen.
    expect(screen.queryByTestId('typing-indicator')).not.toBeInTheDocument()
  })

  it('closes the open conversation the moment membership is revoked', async () => {
    renderShell()

    fireEvent.click(await screen.findByTestId('conversation-item'))
    await screen.findByTestId('virtuoso')

    hub.options?.onMembershipRevoked?.({ conversationId: 'c1' })

    // US3 scenario 3: access ends immediately. Waiting for the list to refetch and quietly drop the
    // row leaves a reader mid-scroll in a conversation they no longer belong to.
    expect(await screen.findByText('Choose a conversation.')).toBeInTheDocument()
  })

  it('leaves a different conversation open when membership elsewhere is revoked', async () => {
    renderShell()

    fireEvent.click(await screen.findByTestId('conversation-item'))
    await screen.findByTestId('virtuoso')

    hub.options?.onMembershipRevoked?.({ conversationId: 'c9' })

    expect(screen.getByTestId('virtuoso')).toBeInTheDocument()
  })

  it('refetches the list when a conversation is created', async () => {
    const { queryClient } = renderShell()

    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')

    hub.options?.onConversationCreated?.()

    // US3 scenario 1. Refetched rather than the event payload being spliced in, because the list
    // projection carries member and unread counts the event does not.
    await waitFor(() => {
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['conversations'] })
    })
  })

  it('applies an edit through the same path as a new message', async () => {
    renderShell()

    fireEvent.click(await screen.findByTestId('conversation-item'))
    await screen.findByTestId('virtuoso')

    hub.options?.onMessageEdited?.(
      aMessage({ id: 'm1', conversationId: 'c1', seq: 1, body: 'corrected' }),
    )

    expect(await screen.findByText('corrected')).toBeInTheDocument()
  })

  it('reflects the transport state in the composer', async () => {
    renderShell()

    fireEvent.click(await screen.findByTestId('conversation-item'))
    await screen.findByTestId('virtuoso')

    // Starts disconnected until the connection says otherwise, so the composer never claims a
    // connection it does not have.
    expect(screen.getByTestId('offline-notice')).toBeInTheDocument()

    hub.options?.onConnectionStateChanged?.(true)

    await waitFor(() => {
      expect(screen.queryByTestId('offline-notice')).not.toBeInTheDocument()
    })
  })
})

describe('typing signals', () => {
  it('forwards them to the connection', async () => {
    renderShell()

    fireEvent.click(await screen.findByTestId('conversation-item'))

    const composer = await screen.findByTestId('composer')

    fireEvent.change(composer, { target: { value: 'hi', selectionStart: 2 } })
    expect(typingCalls.start).toContain('c1')

    fireEvent.blur(composer)
    expect(typingCalls.stop).toContain('c1')
  })
})

describe('selection', () => {
  it('prompts until a conversation is chosen', async () => {
    renderShell()

    await screen.findByTestId('conversation-item')

    expect(screen.getByText('Choose a conversation.')).toBeInTheDocument()
  })

  it('opens the chosen conversation with its own kind and rules', async () => {
    renderShell({
      '/conversations/c1': aConversation({
        id: 'c1',
        kind: 'group',
        historyVisibility: 'full',
        mutedUntil: '2036-01-01T00:00:00Z',
      }),
    })

    fireEvent.click(await screen.findByTestId('conversation-item'))

    // The kind and history rule live on the conversation, not on any message — needed for the
    // group-only member panel and US3 scenario 4's notice.
    expect(await screen.findByTestId('history-notice')).toHaveTextContent(/full history/i)
    expect(screen.getByTestId('mute-toggle')).toHaveTextContent('Unmute')
  })

  it('toggles the create-group form and opens what it creates', async () => {
    const { authorized } = renderShell()

    fireEvent.click(await screen.findByRole('button', { name: 'New group' }))

    expect(screen.getByRole('form', { name: 'Create a group' })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.queryByRole('form', { name: 'Create a group' })).not.toBeInTheDocument()
    expect(authorized).toHaveBeenCalled()
  })
})

describe('search', () => {
  it('is closed until asked for', async () => {
    renderShell()

    await screen.findByTestId('conversation-item')

    expect(screen.queryByTestId('search-input')).not.toBeInTheDocument()
  })

  it('opens and closes', async () => {
    renderShell()

    await screen.findByTestId('conversation-item')

    fireEvent.click(screen.getByRole('button', { name: 'Search' }))

    expect(screen.getByTestId('search-input')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Close search' }))

    expect(screen.queryByTestId('search-input')).not.toBeInTheDocument()
  })

  it('opens the conversation a result points at, and closes itself', async () => {
    renderShell({
      '/search/messages': {
        items: [
          {
            messageId: 'm1',
            conversationId: 'c1',
            conversationName: 'Design',
            seq: 42,
            highlight: 'the <mark>deploy</mark>',
            rank: 0.9,
          },
        ],
        nextCursor: null,
        truncated: false,
      },
    })

    await screen.findByTestId('conversation-item')

    fireEvent.click(screen.getByRole('button', { name: 'Search' }))
    fireEvent.change(screen.getByTestId('search-input'), { target: { value: 'deploy' } })

    const result = await screen.findByTestId('search-results')

    fireEvent.click(at(result.querySelectorAll('button'), 0))

    // FR-031. The panel closes with it — leaving a results list over the conversation it just
    // opened hides the thing the person was looking for.
    expect(await screen.findByTestId('virtuoso')).toBeInTheDocument()
    expect(screen.queryByTestId('search-input')).not.toBeInTheDocument()
  })
})
