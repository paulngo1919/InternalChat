/**
 * T105 — the offline queue never sends a duplicate after reconnect.
 *
 * quickstart V2 step 3: kill the network, send three messages, restore it, all three arrive once and
 * in order. SC-022 states both halves — "never lost, never duplicated" — and they fail in opposite
 * directions, so both are asserted here rather than only the one that is easier to check.
 */

import { describe, expect, it, vi } from 'vitest'

import {
  OfflineQueue,
  memoryQueueStorage,
  type QueuedMessage,
  type SendOutcome,
} from '../src/lib/messages/offlineQueue'
import { ULID_LENGTH, isUlid, newUlid } from '../src/lib/messages/ulid'
import { at } from './at'

const CONVERSATION = '11111111-1111-4111-8111-111111111111'

/** A send that records every attempt and answers from a script. */
function recordingSend(outcomes: SendOutcome[]) {
  const attempts: QueuedMessage[] = []
  let index = 0

  return {
    attempts,
    send: (message: QueuedMessage): Promise<SendOutcome> => {
      attempts.push(message)
      const outcome = outcomes[Math.min(index, outcomes.length - 1)]
      index++
      return Promise.resolve(outcome ?? { status: 'sent' })
    },
  }
}

describe('ULID keys', () => {
  it('are the length and alphabet the server accepts', () => {
    const key = newUlid()

    expect(key).toHaveLength(ULID_LENGTH)
    expect(isUlid(key)).toBe(true)

    // The server's ClientMessageKey.Parse rejects I, L, O, and U. A key containing one is refused
    // with a 400, and the message never sends — from the employee's side, silently.
    expect(key).not.toMatch(/[ILOU]/)
  })

  it('does not collide across many generations', () => {
    const keys = new Set(Array.from({ length: 2000 }, () => newUlid()))

    // A collision would be read by the server as a retry of the colliding message, so the second
    // send would be accepted and then quietly discarded.
    expect(keys.size).toBe(2000)
  })

  it('sorts by time', () => {
    const earlier = newUlid(1_000_000_000_000)
    const later = newUlid(1_000_000_060_000)

    expect(earlier < later).toBe(true)
  })
})

describe('OfflineQueue', () => {
  it('keeps the same key across every retry', async () => {
    const { attempts, send } = recordingSend([
      { status: 'retry' },
      { status: 'retry' },
      { status: 'sent' },
    ])

    const queue = new OfflineQueue(send, memoryQueueStorage())
    const key = queue.enqueue(CONVERSATION, 'hello')

    await queue.flush()
    await queue.flush()
    await queue.flush()

    expect(attempts).toHaveLength(3)

    // THE assertion. A fresh key per attempt would defeat the server's unique index and turn one
    // message into three — the failure FR-011 exists to prevent, and the one the server cannot
    // protect against because it only ever sees distinct keys.
    expect(attempts.every((a) => a.clientMessageKey === key)).toBe(true)
    expect(queue.pending).toHaveLength(0)
  })

  it('preserves composition order across a reconnect', async () => {
    // Offline: three sends all fail.
    const offline = recordingSend([{ status: 'retry' }])
    const storage = memoryQueueStorage()
    const queue = new OfflineQueue(offline.send, storage)

    const first = queue.enqueue(CONVERSATION, 'one', 1_000)
    const second = queue.enqueue(CONVERSATION, 'two', 2_000)
    const third = queue.enqueue(CONVERSATION, 'three', 3_000)

    await queue.flush()
    expect(queue.pending).toHaveLength(3)

    // Back online, on a queue rebuilt from storage as a reload would rebuild it.
    const online = recordingSend([{ status: 'sent' }])
    const reconnected = new OfflineQueue(online.send, storage)

    await reconnected.flush()

    expect(online.attempts.map((a) => a.clientMessageKey)).toEqual([first, second, third])
    expect(online.attempts.map((a) => a.body)).toEqual(['one', 'two', 'three'])
    expect(reconnected.pending).toHaveLength(0)
  })

  it('stops at the first failure so a later message cannot overtake an earlier one', async () => {
    const { attempts, send } = recordingSend([{ status: 'retry' }])
    const queue = new OfflineQueue(send, memoryQueueStorage())

    queue.enqueue(CONVERSATION, 'one')
    queue.enqueue(CONVERSATION, 'two')

    await queue.flush()

    // Only the head was attempted. Continuing past it would let "two" reach the server first and
    // take the lower sequence — and since the server's sequence IS the order, the overtake would be
    // permanent rather than a rendering glitch.
    expect(attempts).toHaveLength(1)
    expect(at(attempts, 0).body).toBe('one')
    expect(queue.pending).toHaveLength(2)
  })

  it('runs one flush at a time', async () => {
    let concurrent = 0
    let peak = 0

    const queue = new OfflineQueue(async () => {
      concurrent++
      peak = Math.max(peak, concurrent)
      await Promise.resolve()
      concurrent--
      return { status: 'sent' } satisfies SendOutcome
    }, memoryQueueStorage())

    queue.enqueue(CONVERSATION, 'one')
    queue.enqueue(CONVERSATION, 'two')

    // A reconnect firing while a flush is already running is the ordinary case, not an edge one.
    await Promise.all([queue.flush(), queue.flush(), queue.flush()])

    expect(peak).toBe(1)
    expect(queue.pending).toHaveLength(0)
  })

  it('drops a permanently rejected message instead of blocking the queue', async () => {
    const { attempts, send } = recordingSend([
      { status: 'rejected', reason: 'body too long' },
      { status: 'sent' },
    ])

    const queue = new OfflineQueue(send, memoryQueueStorage())
    queue.enqueue(CONVERSATION, 'x'.repeat(9000))
    queue.enqueue(CONVERSATION, 'fine')

    await queue.flush()

    // A rejected message that stayed at the head would block every later message forever, and the
    // employee would see an outage rather than one refused send.
    expect(attempts).toHaveLength(2)
    expect(queue.pending).toHaveLength(0)
  })

  it('survives a reload with its keys intact', () => {
    const storage = memoryQueueStorage()
    const first = new OfflineQueue(() => Promise.resolve({ status: 'retry' }), storage)

    const key = first.enqueue(CONVERSATION, 'unsent')

    const afterReload = new OfflineQueue(() => Promise.resolve({ status: 'sent' }), storage)

    // Regenerating the key on reload would duplicate a message the server had already accepted but
    // whose response the tab never saw.
    expect(afterReload.pending.map((m) => m.clientMessageKey)).toEqual([key])
  })

  it('notifies subscribers so the UI can show what is still sending', () => {
    const queue = new OfflineQueue(() => Promise.resolve({ status: 'retry' }), memoryQueueStorage())
    const listener = vi.fn()

    queue.subscribe(listener)
    queue.enqueue(CONVERSATION, 'hello')

    expect(listener).toHaveBeenCalledTimes(1)
    expect(at(at(listener.mock.calls, 0), 0)).toHaveLength(1)
  })
})
