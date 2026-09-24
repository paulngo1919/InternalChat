import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { MessageList } from '../src/features/messages/MessageList'
import type { QueuedMessage } from '../src/lib/messages/offlineQueue'
import { aMessage } from './helpers'
import { at } from './at'

/**
 * The transcript (T101).
 *
 * <b>`react-virtuoso` is replaced with a stub that renders every row.</b> Virtualization is a
 * performance property, and jsdom has no layout — a real Virtuoso measures a zero-height viewport
 * and renders nothing, so every assertion here would pass vacuously against an empty list. The
 * stub renders all rows and exposes `startReached`, which lets the two things worth asserting be
 * asserted: the ordering the component computes, and that reaching the top asks for more history.
 *
 * What this deliberately does not test is that virtualization works. That needs a real browser with
 * real layout, and it belongs in the Playwright suite rather than here.
 */

interface StubProps {
  data: { key: string }[]
  itemContent: (index: number, row: unknown) => React.ReactNode
  computeItemKey: (index: number, row: unknown) => string
  startReached: () => void
  initialTopMostItemIndex: number
  followOutput: string
}

const captured: { props: StubProps | null } = { props: null }

vi.mock('react-virtuoso', () => ({
  Virtuoso: (props: StubProps) => {
    captured.props = props

    return (
      <div data-testid="virtuoso">
        <button
          type="button"
          data-testid="reach-top"
          onClick={() => {
            props.startReached()
          }}
        />
        {props.data.map((row, index) => (
          <div key={props.computeItemKey(index, row)} data-testid="virtuoso-row">
            {props.itemContent(index, row)}
          </div>
        ))}
      </div>
    )
  },
}))

afterEach(() => {
  captured.props = null
  cleanup()
})

/** A queued send, which has no sequence yet. */
function aPending(overrides: Partial<QueuedMessage> = {}): QueuedMessage {
  return {
    clientMessageKey: '01JBXQ7ZPT4M9WYFN2VKC3H6RD',
    conversationId: 'c1',
    body: 'still going',
    mentions: [],
    attachmentIds: [],
    queuedAt: '2026-09-22T09:00:00Z',
    attempts: 0,
    ...overrides,
  } as QueuedMessage
}

function renderList(
  overrides: Partial<Parameters<typeof MessageList>[0]> = {},
): { onLoadOlder: ReturnType<typeof vi.fn> } {
  const onLoadOlder = vi.fn()

  render(
    <MessageList
      messages={[]}
      pending={[]}
      currentEmployeeId="e1"
      onLoadOlder={onLoadOlder}
      hasOlder={false}
      {...overrides}
    />,
  )

  return { onLoadOlder }
}

describe('ordering', () => {
  it('orders by server sequence, not by arrival or timestamp', () => {
    renderList({
      messages: [
        aMessage({ id: 'm3', seq: 3, body: 'third' }),
        aMessage({ id: 'm1', seq: 1, body: 'first' }),
        aMessage({ id: 'm2', seq: 2, body: 'second' }),
      ],
    })

    // FR-012 makes the server sequence the ordering authority, and contracts/signalr-hub.md
    // requires clients to apply by `seq` rather than arrival time — a message recovered by Resync
    // arrives after one pushed live but belongs before it.
    const rows = screen.getAllByTestId('virtuoso-row')

    expect(rows.map((row) => row.textContent)).toEqual(['first', 'second', 'third'])
  })

  it('does not mutate the array it was handed', () => {
    const messages = [aMessage({ id: 'm2', seq: 2 }), aMessage({ id: 'm1', seq: 1 })]

    renderList({ messages })

    // Sorting in place would reorder the caller's cached array — a React Query cache entry, in
    // practice — and the next render would start from a list that had silently changed underneath.
    expect(messages.map((message) => message.id)).toEqual(['m2', 'm1'])
  })

  it('puts pending sends last, whatever sequence the confirmed ones have', () => {
    renderList({
      messages: [aMessage({ id: 'm1', seq: 9, body: 'confirmed' })],
      pending: [aPending({ body: 'not yet' })],
    })

    const rows = screen.getAllByTestId('virtuoso-row')

    // They have no sequence yet, so there is nothing to sort them by. Last is the only position
    // that matches what the sender just did.
    expect(at(rows, 0)).toHaveTextContent('confirmed')
    expect(at(rows, 1)).toHaveTextContent('not yet')
  })
})

describe('rows', () => {
  it('keys a pending row by its client message key', () => {
    renderList({ pending: [aPending({ clientMessageKey: 'KEY-1' })] })

    // The same key the confirmed message will carry. That is what lets the acknowledgement replace
    // the optimistic row instead of appending a second copy the sender then sees twice.
    expect(captured.props?.computeItemKey(0, at(captured.props.data, 0))).toBe('KEY-1')
  })

  it('keys a confirmed row by its message id', () => {
    renderList({ messages: [aMessage({ id: 'm7' })] })

    // Stable per row. Without it, virtuoso reuses DOM nodes by index, and a prepended page of
    // history makes every visible message appear to change content at once.
    expect(captured.props?.computeItemKey(0, at(captured.props.data, 0))).toBe('m7')
  })

  it('renders a deleted message as a tombstone rather than an empty bubble', () => {
    renderList({
      messages: [aMessage({ body: null, deletedAt: '2026-09-22T09:05:00Z' })],
    })

    // The row survives so ordering and sequence continuity hold (data-model.md); the content does
    // not. An empty bubble reads as a rendering bug.
    expect(screen.getByTestId('tombstone')).toHaveTextContent('This message was deleted.')
  })

  it('marks an edited message, and does not mark an unedited one', () => {
    renderList({
      messages: [
        aMessage({ id: 'm1', seq: 1, editedAt: null }),
        aMessage({ id: 'm2', seq: 2, editedAt: '2026-09-22T09:05:00Z' }),
      ],
    })

    // FR-020: an edit is visible as an edit. Silently replacing the text would let someone change
    // what they said after a colleague acted on it, with no trace.
    expect(screen.getAllByTestId('edited-marker')).toHaveLength(1)
  })

  it('does not mark a deleted message as edited', () => {
    renderList({
      messages: [aMessage({ body: null, deletedAt: 'x', editedAt: '2026-09-22T09:05:00Z' })],
    })

    // A tombstone that also said "(edited)" would be describing content nobody can see.
    expect(screen.queryByTestId('edited-marker')).not.toBeInTheDocument()
  })

  it('distinguishes the reader own messages from everyone else', () => {
    renderList({
      messages: [
        aMessage({ id: 'm1', seq: 1, authorId: 'e1' }),
        aMessage({ id: 'm2', seq: 2, authorId: 'e2' }),
      ],
      pending: [aPending()],
    })

    const rows = screen.getAllByTestId(/^(message|pending-message)$/)

    expect(at(rows, 0)).toHaveAttribute('data-mine', 'true')
    expect(at(rows, 1)).toHaveAttribute('data-mine', 'false')

    // A pending message is always the reader's — nobody else's send is in this browser's queue.
    expect(at(rows, 2)).toHaveAttribute('data-mine', 'true')
  })

  it('announces a pending send politely', () => {
    renderList({ pending: [aPending()] })

    const pending = screen.getByTestId('pending-message')

    expect(pending).toHaveTextContent('Sending…')
    expect(pending.querySelector('[aria-live="polite"]')).not.toBeNull()
  })
})

describe('paging', () => {
  it('asks for an older page when the reader reaches the top', () => {
    const { onLoadOlder } = renderList({ hasOlder: true })

    fireEvent.click(screen.getByTestId('reach-top'))

    // FR-013.
    expect(onLoadOlder).toHaveBeenCalledTimes(1)
  })

  it('does not ask when there is nothing older', () => {
    const { onLoadOlder } = renderList({ hasOlder: false })

    fireEvent.click(screen.getByTestId('reach-top'))

    // Otherwise every scroll to the top of a fully-loaded conversation fires a request that can
    // only come back empty.
    expect(onLoadOlder).not.toHaveBeenCalled()
  })

  it('opens at the newest message and follows new ones', () => {
    renderList({
      messages: [aMessage({ id: 'm1', seq: 1 }), aMessage({ id: 'm2', seq: 2 })],
    })

    expect(captured.props?.initialTopMostItemIndex).toBe(1)

    // Follows the bottom, but only while the reader is already there — yanking somebody back
    // mid-read is the single most irritating thing a chat list can do.
    expect(captured.props?.followOutput).toBe('smooth')
  })

  it('clamps the initial index for an empty conversation', () => {
    renderList()

    // rows.length - 1 is -1 with nothing to show, and a negative index throws inside virtuoso.
    expect(captured.props?.initialTopMostItemIndex).toBe(0)
  })
})

describe('attachments', () => {
  const anAttachment = {
    id: 'a1',
    kind: 'image' as const,
    contentType: 'image/png',
    byteSize: 2048,
    durationSeconds: null,
    fileName: 'shot.png',
    scanStatus: 'clean' as const,
    contentUrl: '/api/v1/attachments/a1/content',
    posterUrl: null,
  }

  it('renders a message attachments beneath its text', () => {
    renderList({ messages: [aMessage({ body: 'have a look', attachments: [anAttachment] })] })

    expect(screen.getByText('have a look')).toBeInTheDocument()
    expect(screen.getByRole('img', { name: 'shot.png' })).toBeInTheDocument()
  })

  it('adds nothing to a message that has none', () => {
    renderList({ messages: [aMessage({ body: 'just text' })] })

    // An empty element on every message in a conversation is a layout artefact people ask about.
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })

  it('shows no attachments on a tombstone', () => {
    renderList({
      messages: [aMessage({ body: null, deletedAt: 'x', attachments: [anAttachment] })],
    })

    // FR-019: deleting a message removes its content, and its attachments are its content. The
    // row survives for sequence continuity; nothing of what was said does.
    expect(screen.getByTestId('tombstone')).toBeInTheDocument()
    expect(screen.queryByRole('img')).not.toBeInTheDocument()
  })
})
