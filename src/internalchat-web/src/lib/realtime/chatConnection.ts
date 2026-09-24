/**
 * T103 — the SignalR client, with automatic reconnect and `Resync`.
 *
 * contracts/signalr-hub.md is explicit that the hub is not durable: "An event missed while
 * disconnected is recovered by `Resync`, never by server-side buffering." So reconnecting is only
 * half the job — a client that reconnects without resyncing silently loses every message sent during
 * the gap, and nothing anywhere reports it.
 */

import {
  HttpTransportType,
  type HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'

import type { MessageResponse } from '../api/messages'

/** Server-to-client event names, from contracts/signalr-hub.md. */
export const ChatEvents = {
  MessageReceived: 'MessageReceived',
  MessageEdited: 'MessageEdited',
  MessageDeleted: 'MessageDeleted',
  ConversationCreated: 'ConversationCreated',
  ConversationUpdated: 'ConversationUpdated',
  MembershipRevoked: 'MembershipRevoked',
  ReadStateUpdated: 'ReadStateUpdated',
  TypingChanged: 'TypingChanged',
  PresenceChanged: 'PresenceChanged',
  TokenExpiring: 'TokenExpiring',
} as const

/** `MessageDeleted` carries no body, deliberately (contracts/signalr-hub.md). */
export interface MessageDeletedEvent {
  readonly conversationId: string
  readonly messageId: string
  readonly seq: number
}

/** `TypingChanged` replaces the whole set rather than sending deltas (FR-016). */
export interface TypingChangedEvent {
  readonly conversationId: string
  readonly employeeIds: readonly string[]
}

/** `MembershipRevoked` carries only the id — a removed member has nothing else to learn (US3). */
export interface MembershipRevokedEvent {
  readonly conversationId: string
}

/** What the caller must supply for the connection to be useful. */
export interface ChatConnectionOptions {
  /** Hub URL, usually `/hubs/chat` on the same origin. */
  readonly url: string
  /** Returns a token valid now. Called on every connect and reconnect (D5). */
  readonly accessTokenFactory: () => Promise<string | null>
  /**
   * The highest sequence already applied, per conversation.
   *
   * Read fresh at each reconnect rather than captured once: the gap to close is the one that exists
   * when the connection returns, not the one that existed when it dropped.
   */
  readonly lastSeenSeq: () => Record<string, number>
  /** Applies messages recovered by a resync, and those pushed while connected. */
  readonly onMessages: (messages: readonly MessageResponse[]) => void
  readonly onMessageEdited?: (message: MessageResponse) => void
  readonly onMessageDeleted?: (event: MessageDeletedEvent) => void
  readonly onTypingChanged?: (event: TypingChangedEvent) => void
  /**
   * Added to a conversation (US3 scenario 1) — a new group, or an existing one gained after
   * removal. The payload carries the conversation itself, but the handler only needs to know
   * something changed: refetching the list is simpler than reconciling one row into a cache the
   * conversation list component owns.
   */
  readonly onConversationCreated?: () => void
  /** Removed from a conversation (US3 scenario 3) — stop showing it, without waiting for a refresh. */
  readonly onMembershipRevoked?: (event: MembershipRevokedEvent) => void
  /** Told when the transport comes and goes, so the UI can say so.  */
  readonly onConnectionStateChanged?: (connected: boolean) => void
}

/**
 * Wraps a hub connection so reconnect and resync cannot be separated.
 *
 * Resync runs after `start()` as well as after every reconnect. The first connection of a session
 * has a gap too — everything sent between the last page load and now — and treating that case
 * differently is how a client ends up showing a conversation that is missing its most recent
 * messages until something else happens to refresh it.
 */
export class ChatConnection {
  private readonly options: ChatConnectionOptions
  private readonly connection: HubConnection

  constructor(options: ChatConnectionOptions) {
    this.options = options

    this.connection = new HubConnectionBuilder()
      .withUrl(options.url, {
        // The browser WebSocket API cannot set headers, so the token travels as a query parameter
        // and the API accepts it on the hub path only. Called again on every reconnect, which is
        // what stops a long-lived connection outliving its token (D5).
        accessTokenFactory: async () => (await options.accessTokenFactory()) ?? '',
        transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
      })
      // Default schedule, then every 30s. Indefinite on purpose: a client that gives up after a
      // few minutes shows an employee a dead tab that looks alive, and a laptop closed over lunch
      // is the common case rather than the exception.
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (context) =>
          context.previousRetryCount < 5 ? 2 ** context.previousRetryCount * 1000 : 30_000,
      })
      .configureLogging(LogLevel.Warning)
      .build()

    this.registerHandlers()
  }

  /** Whether the transport is currently up. */
  get connected(): boolean {
    return this.connection.state === HubConnectionState.Connected
  }

  /** Connects and closes the initial gap. */
  async start(): Promise<void> {
    await this.connection.start()
    this.options.onConnectionStateChanged?.(true)
    await this.resync()
  }

  /** Disconnects. Does not sign out — that is the OIDC end-session endpoint's job. */
  async stop(): Promise<void> {
    await this.connection.stop()
  }

  /** Tells the server this employee is typing. Fire-and-forget by contract. */
  async startTyping(conversationId: string): Promise<void> {
    await this.invokeQuietly('StartTyping', conversationId)
  }

  /** Tells the server they have stopped. */
  async stopTyping(conversationId: string): Promise<void> {
    await this.invokeQuietly('StopTyping', conversationId)
  }

  /** Asserts presence. `offline` is inferred from disconnection and never sent (contract). */
  async setPresence(state: 'online' | 'away' | 'dnd'): Promise<void> {
    await this.invokeQuietly('SetPresence', state)
  }

  /**
   * Asks for everything above each conversation's last applied sequence.
   *
   * Failure is swallowed and logged rather than propagated. A resync that throws would reject the
   * caller's `start()` and leave a connected client looking broken; the next reconnect resyncs
   * again, and the history endpoint covers the gap in the meantime.
   */
  private async resync(): Promise<void> {
    const lastSeen = this.options.lastSeenSeq()

    if (Object.keys(lastSeen).length === 0) {
      return
    }

    try {
      const recovered = await this.connection.invoke<Record<string, MessageResponse[]>>(
        'Resync',
        lastSeen,
      )

      // Flattened and applied in one pass. Every event carries `seq`, and the client applies by
      // `seq` rather than arrival order (contracts/signalr-hub.md), so a message recovered here and
      // also pushed live is applied once.
      const messages = Object.values(recovered).flat()

      if (messages.length > 0) {
        this.options.onMessages(messages)
      }
    } catch (error) {
      console.warn('Resync failed; history will cover the gap until the next reconnect.', error)
    }
  }

  private registerHandlers(): void {
    this.connection.on(ChatEvents.MessageReceived, (message: MessageResponse) => {
      this.options.onMessages([message])
    })

    this.connection.on(ChatEvents.MessageEdited, (message: MessageResponse) => {
      this.options.onMessageEdited?.(message)
    })

    this.connection.on(ChatEvents.MessageDeleted, (event: MessageDeletedEvent) => {
      this.options.onMessageDeleted?.(event)
    })

    this.connection.on(ChatEvents.TypingChanged, (event: TypingChangedEvent) => {
      this.options.onTypingChanged?.(event)
    })

    this.connection.on(ChatEvents.ConversationCreated, () => {
      this.options.onConversationCreated?.()
    })

    this.connection.on(ChatEvents.MembershipRevoked, (event: MembershipRevokedEvent) => {
      this.options.onMembershipRevoked?.(event)
    })

    this.connection.onreconnecting(() => {
      this.options.onConnectionStateChanged?.(false)
    })

    // The reconnect handler is where the contract's guarantee is actually honoured. Reconnecting
    // without this leaves every message sent during the outage permanently absent from the client,
    // because the hub buffered nothing for it.
    this.connection.onreconnected(() => {
      this.options.onConnectionStateChanged?.(true)
      void this.resync()
    })

    this.connection.onclose(() => {
      this.options.onConnectionStateChanged?.(false)
    })
  }

  /**
   * Invokes a hub method, ignoring failure.
   *
   * For typing and presence only, both of which the contract calls ephemeral. Surfacing an error
   * from a keystroke would put an error toast in front of an employee for something that fixes
   * itself in ten seconds when the TTL expires.
   */
  private async invokeQuietly(method: string, ...args: unknown[]): Promise<void> {
    if (!this.connected) {
      return
    }

    try {
      await this.connection.invoke(method, ...args)
    } catch {
      // Ephemeral by contract; the TTL cleans up either way.
    }
  }
}
