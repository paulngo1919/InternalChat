/**
 * T102 — the offline send queue.
 *
 * quickstart V2 step 3 is the scenario: kill the network, send three messages, restore it, and all
 * three arrive once, in order. That is FR-018 and SC-022 ("never lost, never duplicated"), and both
 * halves are easy to get wrong in opposite directions — a queue that drops on failure loses
 * messages, and one that retries without a stable key duplicates them.
 *
 * The key is generated when the message is *enqueued*, never when it is sent. Every retry of an
 * entry carries the same `clientMessageKey`, so the server's unique constraint collapses them into
 * one message and returns the original (FR-011).
 */

import { newUlid } from './ulid'

/** A send that has been accepted from the composer but not yet acknowledged by the server. */
export interface QueuedMessage {
  /** The idempotency key. Assigned at enqueue and never regenerated. */
  readonly clientMessageKey: string
  readonly conversationId: string
  readonly body: string
  /** When the composer accepted it, for ordering and for showing "sending…" age. */
  readonly queuedAt: number
  /** How many send attempts have been made. Diagnostics only; it never affects the key. */
  readonly attempts: number
  /**
   * Colleagues mentioned by name (FR-015), when the composer recognised any. Candidates only —
   * the server narrows this to active members at send time, so a stale or mistyped mention costs
   * nothing and notifies nobody.
   */
  readonly mentions?: readonly string[]
  /**
   * Attachments already uploaded and scanned-pending, quoted back on send (FR-021).
   *
   * Ids only. The bytes went straight to storage through a presigned ticket before this
   * message was ever composed, so a queued send carries nothing large — which is what keeps a
   * 500 MB video out of `sessionStorage`, where it would blow the quota and take the whole
   * outbox with it.
   */
  readonly attachmentIds?: readonly string[]
}

/** What a send attempt reported. */
export type SendOutcome =
  | { readonly status: 'sent' }
  /** The server refused it in a way retrying cannot fix — a 4xx other than 429. */
  | { readonly status: 'rejected'; readonly reason: string }
  /** Offline, timed out, 5xx, or 429. Keep it and try again. */
  | { readonly status: 'retry' }

/** Sends one queued message. Returns what happened; never throws for an expected failure. */
export type SendAttempt = (message: QueuedMessage) => Promise<SendOutcome>

/** Where the queue survives a reload. */
export interface QueueStorage {
  read(): QueuedMessage[]
  write(messages: QueuedMessage[]): void
}

/**
 * `sessionStorage`-backed persistence.
 *
 * Survives a reload, which is the case that matters: an employee whose tab crashed mid-send should
 * not lose the message. Deliberately not `localStorage` — a queue shared across tabs would be
 * drained by two flushers at once, and while the server would deduplicate the sends, the UI in each
 * tab would disagree about what is still pending.
 */
export function sessionQueueStorage(key = 'internalchat.outbox'): QueueStorage {
  return {
    read(): QueuedMessage[] {
      try {
        const raw = sessionStorage.getItem(key)
        return raw ? (JSON.parse(raw) as QueuedMessage[]) : []
      } catch {
        // A corrupted queue is dropped rather than allowed to wedge every future send. The
        // alternative — throwing on read — leaves the composer permanently broken with no way for
        // the employee to recover.
        return []
      }
    },
    write(messages: QueuedMessage[]): void {
      try {
        sessionStorage.setItem(key, JSON.stringify(messages))
      } catch {
        // Storage full or blocked. The in-memory queue still works for this session; losing
        // durability is better than losing the send.
      }
    },
  }
}

/** In-memory storage, for tests and for a browser that blocks storage entirely. */
export function memoryQueueStorage(): QueueStorage {
  let messages: QueuedMessage[] = []

  return {
    read: () => [...messages],
    write: (next) => {
      messages = [...next]
    },
  }
}

/**
 * Holds pending sends and flushes them in order.
 *
 * <b>Single-flight.</b> `flush` returns the in-progress run rather than starting a second one.
 * Without that, a reconnect firing while a flush is already running would send every pending entry
 * twice — the server would deduplicate, so no duplicate message would appear, but the client would
 * still have raced itself and the second run's outcomes would be applied to a queue the first had
 * already changed.
 */
export class OfflineQueue {
  private readonly storage: QueueStorage
  private readonly send: SendAttempt
  private readonly listeners = new Set<(pending: QueuedMessage[]) => void>()

  private queue: QueuedMessage[]
  private inFlight: Promise<void> | null = null

  constructor(send: SendAttempt, storage: QueueStorage = sessionQueueStorage()) {
    this.send = send
    this.storage = storage
    this.queue = storage.read()
  }

  /** Everything still waiting, oldest first. */
  get pending(): readonly QueuedMessage[] {
    return this.queue
  }

  /** Subscribes to queue changes, for rendering optimistic bubbles. */
  subscribe(listener: (pending: QueuedMessage[]) => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  /**
   * Accepts a message from the composer and returns its key.
   *
   * The key is returned so the caller can render an optimistic bubble identified the same way the
   * server will identify the real message — which is what lets the two be reconciled instead of
   * both being shown.
   */
  enqueue(
    conversationId: string,
    body: string,
    now: number = Date.now(),
    mentions?: readonly string[],
    attachmentIds?: readonly string[],
  ): string {
    const clientMessageKey = newUlid(now)

    this.queue = [
      ...this.queue,
      {
        clientMessageKey,
        conversationId,
        body,
        queuedAt: now,
        attempts: 0,
        // Omitted entirely rather than set to an empty array under `exactOptionalPropertyTypes` —
        // and omitted rather than `undefined`, which that same setting refuses on an optional key.
        ...(mentions && mentions.length > 0 ? { mentions } : {}),
        ...(attachmentIds && attachmentIds.length > 0 ? { attachmentIds } : {}),
      },
    ]

    this.persist()
    return clientMessageKey
  }

  /**
   * Attempts every pending send, oldest first.
   *
   * Stops at the first entry that needs retrying, deliberately: messages within a conversation must
   * arrive in the order they were composed, and continuing past a failure would let a later message
   * overtake an earlier one. Ordering on the server comes from its own sequence, so an overtake
   * would be permanent rather than merely visible.
   */
  flush(): Promise<void> {
    this.inFlight ??= this.drain().finally(() => {
      this.inFlight = null
    })

    return this.inFlight
  }

  private async drain(): Promise<void> {
    while (this.queue.length > 0) {
      const next = this.queue[0]
      if (!next) {
        return
      }

      const outcome = await this.send({ ...next, attempts: next.attempts + 1 })

      if (outcome.status === 'retry') {
        // Left at the head with its attempt count bumped, so the key stays the same and the UI can
        // show that it is still trying.
        this.queue = [{ ...next, attempts: next.attempts + 1 }, ...this.queue.slice(1)]
        this.persist()
        return
      }

      // Sent or permanently rejected — either way it leaves the queue. A rejected message that
      // stayed would block every message behind it forever.
      this.queue = this.queue.slice(1)
      this.persist()
    }
  }

  private persist(): void {
    this.storage.write(this.queue)

    for (const listener of this.listeners) {
      listener([...this.queue])
    }
  }
}
