import { beforeEach, describe, expect, it, vi } from 'vitest'
import { at } from './at'

/**
 * The SignalR client, and the one guarantee it exists to keep.
 *
 * contracts/signalr-hub.md is explicit that the hub is not durable: "An event missed while
 * disconnected is recovered by `Resync`, never by server-side buffering." Reconnecting is therefore
 * only half the job. A client that reconnects without resyncing silently loses every message sent
 * during the gap — no error, no gap in the sequence the user can see, nothing in a log. The
 * conversation simply has a hole in it, and the only person who finds out is whoever asks why
 * nobody replied.
 *
 * That is what most of this file asserts. The SignalR transport itself is mocked, because what is
 * being tested is which callbacks fire and in what order, and a real WebSocket would only make
 * those assertions slower and flakier.
 */

/** The mock connection the builder hands back. Captured so tests can drive it. */
interface MockConnection {
  state: number
  start: ReturnType<typeof vi.fn>
  stop: ReturnType<typeof vi.fn>
  invoke: ReturnType<typeof vi.fn>
  on: ReturnType<typeof vi.fn>
  onreconnecting: ReturnType<typeof vi.fn>
  onreconnected: ReturnType<typeof vi.fn>
  onclose: ReturnType<typeof vi.fn>
  handlers: Map<string, (...args: unknown[]) => void>
  lifecycle: Map<string, () => void>
  builtWith: { url: string; options: Record<string, unknown> } | null
  retryPolicy: { nextRetryDelayInMilliseconds: (context: { previousRetryCount: number }) => number }
}

const mock: { connection: MockConnection | null } = { connection: null }

vi.mock('@microsoft/signalr', () => {
  const HttpTransportType = { WebSockets: 1, LongPolling: 4 }
  const HubConnectionState = { Disconnected: 0, Connected: 1 }
  const LogLevel = { Warning: 3 }

  class HubConnectionBuilder {
    private connection: MockConnection

    constructor() {
      const handlers = new Map<string, (...args: unknown[]) => void>()
      const lifecycle = new Map<string, () => void>()

      this.connection = {
        state: HubConnectionState.Disconnected,
        start: vi.fn(function (this: MockConnection) {
          this.state = HubConnectionState.Connected
          return Promise.resolve()
        }),
        stop: vi.fn(function (this: MockConnection) {
          this.state = HubConnectionState.Disconnected
          return Promise.resolve()
        }),
        invoke: vi.fn(() => Promise.resolve({})),
        on: vi.fn((name: string, handler: (...args: unknown[]) => void) => {
          handlers.set(name, handler)
        }),
        onreconnecting: vi.fn((handler: () => void) => lifecycle.set('reconnecting', handler)),
        onreconnected: vi.fn((handler: () => void) => lifecycle.set('reconnected', handler)),
        onclose: vi.fn((handler: () => void) => lifecycle.set('close', handler)),
        handlers,
        lifecycle,
        builtWith: null,
        retryPolicy: { nextRetryDelayInMilliseconds: () => 0 },
      }

      // `start` and `stop` mutate the connection, so they need it as `this`.
      this.connection.start = vi.fn(() => {
        this.connection.state = HubConnectionState.Connected
        return Promise.resolve()
      })
      this.connection.stop = vi.fn(() => {
        this.connection.state = HubConnectionState.Disconnected
        return Promise.resolve()
      })

      mock.connection = this.connection
    }

    withUrl(url: string, options: Record<string, unknown>) {
      this.connection.builtWith = { url, options }
      return this
    }

    withAutomaticReconnect(policy: {
      nextRetryDelayInMilliseconds: (context: { previousRetryCount: number }) => number
    }) {
      this.connection.retryPolicy = policy
      return this
    }

    configureLogging() {
      return this
    }

    build() {
      return this.connection
    }
  }

  return { HttpTransportType, HubConnectionState, LogLevel, HubConnectionBuilder }
})

import type { ChatConnectionOptions } from '../src/lib/realtime/chatConnection'

const { ChatConnection, ChatEvents } = await import('../src/lib/realtime/chatConnection')

/** A connection with recording callbacks and controllable inputs. */
function build(overrides: Partial<ChatConnectionOptions> = {}) {
  const received: unknown[][] = []
  const edited: unknown[] = []
  const deleted: unknown[] = []
  const typing: unknown[] = []
  const revoked: unknown[] = []
  const states: boolean[] = []
  let created = 0

  const connection = new ChatConnection({
    url: '/hubs/chat',
    accessTokenFactory: () => Promise.resolve('token'),
    lastSeenSeq: () => ({ c1: 7 }),
    onMessages: (messages) => received.push([...messages]),
    onMessageEdited: (message) => edited.push(message),
    onMessageDeleted: (event) => deleted.push(event),
    onTypingChanged: (event) => typing.push(event),
    onConversationCreated: () => {
      created += 1
    },
    onMembershipRevoked: (event) => revoked.push(event),
    onConnectionStateChanged: (connected) => states.push(connected),
    ...overrides,
  })

  return {
    connection,
    received,
    edited,
    deleted,
    typing,
    revoked,
    states,
    createdCount: () => created,
    hub: () => mock.connection as MockConnection,
  }
}

beforeEach(() => {
  mock.connection = null
  vi.restoreAllMocks()
})

describe('connecting', () => {
  it('supplies a token on every connect, falling back to empty rather than undefined', async () => {
    const { hub } = build({ accessTokenFactory: () => Promise.resolve(null) })

    const factory = hub().builtWith?.options.accessTokenFactory as () => Promise<string>

    // The browser WebSocket API cannot set headers, so the token travels as a query parameter.
    // undefined would be serialised into the URL as the string "undefined"; empty is at least
    // refused cleanly by the server.
    await expect(factory()).resolves.toBe('')
  })

  it('offers long polling as well as WebSockets', () => {
    const { hub } = build()

    // A corporate proxy that blocks WebSocket upgrades is the ordinary case, not the exception.
    // With only WebSockets configured those employees get a tab that never connects.
    expect(hub().builtWith?.options.transport).toBe(1 | 4)
  })

  it('backs off exponentially and then retries indefinitely', () => {
    const { hub } = build()

    const delay = hub().retryPolicy.nextRetryDelayInMilliseconds

    expect(delay({ previousRetryCount: 0 })).toBe(1000)
    expect(delay({ previousRetryCount: 3 })).toBe(8000)

    // Indefinite on purpose. A client that gave up after a few minutes would show an employee a
    // dead tab that looks alive, and a laptop closed over lunch is the common case.
    expect(delay({ previousRetryCount: 5 })).toBe(30_000)
    expect(delay({ previousRetryCount: 500 })).toBe(30_000)
  })

  it('reports the transport coming up, and resyncs the initial gap', async () => {
    const { connection, states, received, hub } = build()

    hub().invoke.mockResolvedValue({ c1: [{ id: 'm1', seq: 8 }] })

    await connection.start()

    expect(states).toEqual([true])
    expect(connection.connected).toBe(true)

    // The first connection of a session has a gap too — everything sent between the last page load
    // and now. Treating it as a special case is how a client ends up showing a conversation missing
    // its most recent messages until something else happens to refresh it.
    expect(hub().invoke).toHaveBeenCalledWith('Resync', { c1: 7 })
    expect(received).toEqual([[{ id: 'm1', seq: 8 }]])
  })

  it('stops without signing out', async () => {
    const { connection, hub } = build()

    await connection.start()
    await connection.stop()

    expect(hub().stop).toHaveBeenCalled()
    expect(connection.connected).toBe(false)
  })
})

describe('resync', () => {
  it('is skipped when the client holds nothing to catch up from', async () => {
    const { connection, hub } = build({ lastSeenSeq: () => ({}) })

    await connection.start()

    // Nothing applied yet means nothing to recover. Asking anyway would make the server compute a
    // page of history the client is about to fetch by its ordinary route.
    expect(hub().invoke).not.toHaveBeenCalled()
  })

  it('applies recovered messages from every conversation in one pass', async () => {
    const { connection, received, hub } = build({ lastSeenSeq: () => ({ c1: 7, c2: 2 }) })

    hub().invoke.mockResolvedValue({
      c1: [{ id: 'm1', seq: 8 }],
      c2: [{ id: 'm2', seq: 3 }],
    })

    await connection.start()

    // Flattened. Every event carries `seq`, and the client applies by `seq` rather than arrival
    // order, so a message recovered here and also pushed live is applied once.
    expect(at(received, 0)).toHaveLength(2)
  })

  it('does not call back when the gap turned out to be empty', async () => {
    const { connection, received, hub } = build()

    hub().invoke.mockResolvedValue({ c1: [] })

    await connection.start()

    expect(received).toEqual([])
  })

  it('survives a failed resync rather than rejecting the caller', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined)
    const { connection, hub } = build()

    hub().invoke.mockRejectedValue(new Error('hub is busy'))

    // A resync that threw would reject start() and leave a connected client looking broken. The
    // next reconnect resyncs again, and the history endpoint covers the gap in the meantime.
    await expect(connection.start()).resolves.toBeUndefined()
    expect(connection.connected).toBe(true)
    expect(warn).toHaveBeenCalled()
  })

  it('reads the gap fresh at each reconnect rather than capturing it once', async () => {
    let seq = 7
    const { connection, hub } = build({ lastSeenSeq: () => ({ c1: seq }) })

    await connection.start()
    expect(hub().invoke).toHaveBeenLastCalledWith('Resync', { c1: 7 })

    seq = 41
    hub().lifecycle.get('reconnected')?.()
    await Promise.resolve()

    // The gap to close is the one that exists when the connection returns, not the one that existed
    // when it dropped — the client kept applying messages from history in between.
    expect(hub().invoke).toHaveBeenLastCalledWith('Resync', { c1: 41 })
  })
})

describe('lifecycle', () => {
  it('reports the transport going away and coming back', async () => {
    const { connection, states, hub } = build()

    await connection.start()

    hub().lifecycle.get('reconnecting')?.()
    hub().lifecycle.get('reconnected')?.()
    hub().lifecycle.get('close')?.()

    expect(states).toEqual([true, false, true, false])
  })

  it('resyncs on reconnect, which is where the contract is actually honoured', async () => {
    const { connection, hub } = build()

    await connection.start()
    hub().invoke.mockClear()

    hub().lifecycle.get('reconnected')?.()
    await Promise.resolve()

    // Reconnecting without this leaves every message sent during the outage permanently absent,
    // because the hub buffered nothing for this client.
    expect(hub().invoke).toHaveBeenCalledWith('Resync', { c1: 7 })
  })
})

describe('server events', () => {
  it('applies a pushed message', async () => {
    const { connection, received, hub } = build()

    await connection.start()
    hub().handlers.get(ChatEvents.MessageReceived)?.({ id: 'm1', seq: 9 })

    expect(received.at(-1)).toEqual([{ id: 'm1', seq: 9 }])
  })

  it('routes edits, deletes, typing, creation and revocation to their handlers', async () => {
    const context = build()

    await context.connection.start()

    const hub = context.hub()

    hub.handlers.get(ChatEvents.MessageEdited)?.({ id: 'm1' })
    hub.handlers.get(ChatEvents.MessageDeleted)?.({ conversationId: 'c1', messageId: 'm1', seq: 9 })
    hub.handlers.get(ChatEvents.TypingChanged)?.({ conversationId: 'c1', employeeIds: ['e2'] })
    hub.handlers.get(ChatEvents.ConversationCreated)?.({ id: 'c2' })
    hub.handlers.get(ChatEvents.MembershipRevoked)?.({ conversationId: 'c1' })

    expect(context.edited).toEqual([{ id: 'm1' }])
    expect(context.deleted).toEqual([{ conversationId: 'c1', messageId: 'm1', seq: 9 }])
    expect(context.typing).toEqual([{ conversationId: 'c1', employeeIds: ['e2'] }])
    expect(context.createdCount()).toBe(1)
    expect(context.revoked).toEqual([{ conversationId: 'c1' }])
  })

  it('tolerates a caller that supplied no optional handlers', async () => {
    const connection = new ChatConnection({
      url: '/hubs/chat',
      accessTokenFactory: () => Promise.resolve('t'),
      lastSeenSeq: () => ({}),
      onMessages: () => undefined,
    })

    await connection.start()

    const hub = mock.connection as MockConnection

    // Optional handlers are optional. Every one of these would be a TypeError in a live session if
    // the optional-call operators were dropped — and the only symptom would be the connection
    // appearing to stop delivering events.
    expect(() => {
      hub.handlers.get(ChatEvents.MessageEdited)?.({})
      hub.handlers.get(ChatEvents.MessageDeleted)?.({})
      hub.handlers.get(ChatEvents.TypingChanged)?.({})
      hub.handlers.get(ChatEvents.ConversationCreated)?.({})
      hub.handlers.get(ChatEvents.MembershipRevoked)?.({})
      hub.lifecycle.get('reconnecting')?.()
      hub.lifecycle.get('close')?.()
    }).not.toThrow()
  })
})

describe('ephemeral signals', () => {
  it('sends typing and presence while connected', async () => {
    const { connection, hub } = build()

    await connection.start()
    hub().invoke.mockClear()

    await connection.startTyping('c1')
    await connection.stopTyping('c1')
    await connection.setPresence('away')

    expect(hub().invoke).toHaveBeenCalledWith('StartTyping', 'c1')
    expect(hub().invoke).toHaveBeenCalledWith('StopTyping', 'c1')
    expect(hub().invoke).toHaveBeenCalledWith('SetPresence', 'away')
  })

  it('does not send them while disconnected', async () => {
    const { connection, hub } = build()

    // Never started. Invoking on a disconnected hub throws, and a keystroke is not worth an
    // exception the composer would then have to catch.
    await connection.startTyping('c1')

    expect(hub().invoke).not.toHaveBeenCalled()
  })

  it('swallows a failure, because these are ephemeral by contract', async () => {
    const { connection, hub } = build()

    await connection.start()
    hub().invoke.mockRejectedValue(new Error('hub is busy'))

    // Surfacing an error from a keystroke would put an error toast in front of an employee for
    // something that fixes itself in ten seconds when the TTL expires.
    await expect(connection.startTyping('c1')).resolves.toBeUndefined()
    await expect(connection.setPresence('dnd')).resolves.toBeUndefined()
  })
})
