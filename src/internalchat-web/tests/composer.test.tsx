import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { Composer } from '../src/features/messages/Composer'
import {
  OfflineQueue,
  type QueuedMessage,
  type SendOutcome,
} from '../src/lib/messages/offlineQueue'
import { at } from './at'

/**
 * The composer (T102).
 *
 * The component holds no send logic — the ULID key, the ordering, the single-flight flush and the
 * persistence all live in `OfflineQueue`, where `tests/offline-queue.test.ts` covers them. What is
 * left here is everything between a keystroke and that `enqueue` call, and two of those are easy to
 * get subtly wrong in ways nobody notices for months:
 *
 * <b>Mentions are pruned against the body at send time.</b> The map of inserted text to employee id
 * is never pruned as the text is edited, so a `@Name` that has been backspaced away would otherwise
 * still notify that person — who then gets a notification pointing at a message that does not
 * mention them.
 *
 * <b>Typing is withdrawn on unmount.</b> Otherwise switching conversations leaves the previous one
 * showing this employee as typing until the server's TTL expires, and the employee has no idea.
 */

/** A queue whose enqueue is observable and whose storage never touches sessionStorage. */
function newQueue() {
  const enqueued: { conversationId: string; body: string; mentions?: readonly string[] }[] = []

  let stored: QueuedMessage[] = []

  const queue = new OfflineQueue(
    () => Promise.resolve<SendOutcome>({ status: 'sent' }),
    {
      read: () => stored,
      write: (value: QueuedMessage[]) => {
        stored = value
      },
    },
  )

  const original = queue.enqueue.bind(queue)

  queue.enqueue = (conversationId, body, now, mentions) => {
    // Spread rather than assigned: under `exactOptionalPropertyTypes` an optional key does not
    // accept an explicit `undefined`, and "absent" is what these tests assert against.
    enqueued.push({ conversationId, body, ...(mentions === undefined ? {} : { mentions }) })
    return original(conversationId, body, now, mentions)
  }

  return { queue, enqueued }
}

function renderComposer(overrides: Partial<Parameters<typeof Composer>[0]> = {}) {
  const context = newQueue()
  const onEnqueued = vi.fn()
  const onStartTyping = vi.fn()
  const onStopTyping = vi.fn()

  const result = render(
    <Composer
      conversationId="c1"
      queue={context.queue}
      onEnqueued={onEnqueued}
      onStartTyping={onStartTyping}
      onStopTyping={onStopTyping}
      connected
      {...overrides}
    />,
  )

  return { ...context, onEnqueued, onStartTyping, onStopTyping, result }
}

/** The textarea. */
const composer = () => screen.getByTestId<HTMLTextAreaElement>('composer')

/** Types into the composer, keeping the caret at the end as a browser would. */
function type(text: string) {
  const element = composer()

  fireEvent.change(element, { target: { value: text, selectionStart: text.length } })
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
})

afterEach(() => {
  vi.useRealTimers()
  cleanup()
})

describe('sending', () => {
  it('enqueues rather than sending, and clears the input at once', () => {
    const { enqueued, onEnqueued } = renderComposer()

    type('hello')
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    // Enqueued, not sent. The input clears immediately and the message renders as pending, which is
    // what makes the composer feel instant on a slow connection — and it is honest, because the
    // queue really will deliver it.
    expect(enqueued).toEqual([{ conversationId: 'c1', body: 'hello', mentions: undefined }])
    expect(composer().value).toBe('')
    expect(onEnqueued).toHaveBeenCalled()
  })

  it('trims, and refuses a message that is only whitespace', () => {
    const { enqueued } = renderComposer()

    type('   ')

    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()

    fireEvent.submit(composer().closest('form') as HTMLFormElement)

    expect(enqueued).toEqual([])

    type('  spaced  ')
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    expect(at(enqueued, 0).body).toBe('spaced')
  })

  it('sends on Enter and inserts a newline on Shift+Enter', () => {
    const { enqueued } = renderComposer()

    type('one')
    fireEvent.keyDown(composer(), { key: 'Enter', shiftKey: true })

    // A textarea rather than an input precisely so a multi-line message — a pasted stack trace,
    // most often — stays readable.
    expect(enqueued).toEqual([])

    fireEvent.keyDown(composer(), { key: 'Enter' })

    expect(enqueued).toHaveLength(1)
  })

  it('refuses a body over the server maximum, saying how long it is', () => {
    const { enqueued } = renderComposer()

    type('x'.repeat(8001))

    // Not a `maxLength` attribute. Silently truncating a pasted message loses the end of it without
    // saying so; refusing and explaining does not.
    expect(composer()).toHaveAttribute('aria-invalid', 'true')
    expect(composer()).toHaveAttribute('aria-describedby', 'composer-error')

    const alert = screen.getByRole('alert')

    expect(alert).toHaveTextContent('8,000 characters')
    expect(alert).toHaveTextContent('8,001')

    fireEvent.keyDown(composer(), { key: 'Enter' })

    // Refused on both paths. Disabling the button alone left Enter open, and this test found that:
    // the message was enqueued, refused by the server as a 422, and then dropped by the queue as a
    // permanent rejection — so the sender's text disappeared with nothing said about why.
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()
    expect(enqueued).toHaveLength(0)

    // And the text is still there to shorten, rather than cleared by a send that did not happen.
    expect(composer().value).toHaveLength(8001)
  })

  it('accepts a body of exactly the maximum', () => {
    renderComposer()

    type('x'.repeat(8000))

    // Inclusive. An off-by-one here rejects the exact length the server accepts.
    expect(composer()).toHaveAttribute('aria-invalid', 'false')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('typing signals', () => {
  it('announces typing once, not on every keystroke', () => {
    const { onStartTyping } = renderComposer()

    type('h')
    type('he')
    type('hel')

    expect(onStartTyping).toHaveBeenCalledTimes(1)
    expect(onStartTyping).toHaveBeenCalledWith('c1')
  })

  it('withdraws the signal after an idle period', () => {
    const { onStopTyping } = renderComposer()

    type('hello')

    expect(onStopTyping).not.toHaveBeenCalled()

    act(() => {
      vi.advanceTimersByTime(3_000)
    })

    expect(onStopTyping).toHaveBeenCalledWith('c1')
  })

  it('withdraws it on send, on blur, and on unmount', () => {
    const first = renderComposer()

    type('hello')
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    expect(first.onStopTyping).toHaveBeenCalledWith('c1')

    cleanup()

    const second = renderComposer()
    type('hello')
    fireEvent.blur(composer())
    expect(second.onStopTyping).toHaveBeenCalledWith('c1')

    cleanup()

    const third = renderComposer()
    type('hello')
    third.onStopTyping.mockClear()
    third.result.unmount()

    // Without this, switching conversations leaves the previous one showing this employee as typing
    // until the server's TTL expires.
    expect(third.onStopTyping).toHaveBeenCalledWith('c1')
  })

  it('works without typing handlers at all', () => {
    const { enqueued } = renderComposer({ onStartTyping: undefined, onStopTyping: undefined })

    expect(() => {
      type('hello')
      fireEvent.blur(composer())
      fireEvent.click(screen.getByRole('button', { name: 'Send' }))
    }).not.toThrow()

    expect(enqueued).toHaveLength(1)
  })
})

describe('mentions', () => {
  const candidates = [
    { id: 'e2', displayName: 'An Nguyen' },
    { id: 'e3', displayName: 'Bình Tran' },
  ]

  it('offers candidates only once an @ query is in progress', () => {
    renderComposer({ mentionCandidates: candidates })

    expect(screen.queryByTestId('mention-autocomplete')).not.toBeInTheDocument()

    type('hi @An')

    expect(screen.getByTestId('mention-autocomplete')).toBeInTheDocument()
  })

  it('offers nothing when the conversation has no candidates', () => {
    renderComposer({ mentionCandidates: [] })

    type('hi @An')

    // A direct conversation. Mentioning the one other participant tells them nothing they do not
    // already know, and the popup would be pure noise.
    expect(screen.queryByTestId('mention-autocomplete')).not.toBeInTheDocument()
  })

  it('inserts the name and sends the id behind it', () => {
    const { enqueued } = renderComposer({ mentionCandidates: candidates })

    type('hi @An')
    fireEvent.mouseDown(at(screen.getAllByTestId('mention-option'), 0))

    expect(composer().value).toBe('hi @An Nguyen ')

    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    expect(at(enqueued, 0).mentions).toEqual(['e2'])
  })

  it('drops a mention whose text the sender removed before sending', () => {
    const { enqueued } = renderComposer({ mentionCandidates: candidates })

    type('hi @An')
    fireEvent.mouseDown(at(screen.getAllByTestId('mention-option'), 0))

    // Backspaced the inserted name away. The map is not pruned as the text is edited, so without
    // the check at send time this would still notify An Nguyen — about a message that no longer
    // mentions them anywhere.
    type('hi ')
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    expect(at(enqueued, 0).mentions).toBeUndefined()
  })

  it('forgets inserted mentions once a message is sent', () => {
    const { enqueued } = renderComposer({ mentionCandidates: candidates })

    type('hi @An')
    fireEvent.mouseDown(at(screen.getAllByTestId('mention-option'), 0))
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    // A second message that happens to contain the same text must not inherit the first one's
    // resolved id — the sender typed it rather than picking it, and may mean someone else entirely.
    type('hi @An Nguyen again')
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    expect(at(enqueued, 1).mentions).toBeUndefined()
  })

  it('closes the popup once a candidate is chosen', () => {
    renderComposer({ mentionCandidates: candidates })

    type('hi @An')
    fireEvent.mouseDown(at(screen.getAllByTestId('mention-option'), 0))

    expect(screen.queryByTestId('mention-autocomplete')).not.toBeInTheDocument()
  })
})

describe('offline', () => {
  it('says so, and leaves the composer usable', () => {
    const { enqueued } = renderComposer({ connected: false })

    expect(screen.getByTestId('offline-notice')).toHaveTextContent(/delivered when the connection/i)

    // The queue holds the message and delivers it on reconnect (FR-018), so disabling the input
    // while offline would take away the one thing that still works — and quickstart V2 step 3
    // depends on being able to type three messages with the network down.
    type('while offline')
    fireEvent.click(screen.getByRole('button', { name: 'Send' }))

    expect(enqueued).toHaveLength(1)
  })

  it('says nothing while connected', () => {
    renderComposer({ connected: true })

    expect(screen.queryByTestId('offline-notice')).not.toBeInTheDocument()
  })
})

describe('labelling', () => {
  it('labels the input for assistive technology', () => {
    renderComposer()

    // A placeholder is not a label: it disappears the moment someone types, and screen readers
    // treat it as a hint rather than a name.
    expect(screen.getByLabelText('Message')).toBe(composer())
  })
})

describe('attachments', () => {
  // jsdom implements neither, and the upload path takes a local preview URL for every image
  // before it asks the server for anything — so without these the upload rejects before the
  // request is made, and the test would report "no upload" for the wrong reason.
  beforeEach(() => {
    URL.createObjectURL = vi.fn(() => 'blob:preview')
    URL.revokeObjectURL = vi.fn()
  })

  afterEach(() => {
    Reflect.deleteProperty(URL, 'createObjectURL')
    Reflect.deleteProperty(URL, 'revokeObjectURL')
  })

  /** An upload API that reserves instantly and a storage PUT that always succeeds. */
  function uploadApi() {
    return {
      requestUpload: vi.fn(() =>
        Promise.resolve({
          attachmentId: 'a1',
          uploadUrl: 'https://minio.test/quarantine/c1/a1',
          expiresAt: 'x',
        }),
      ),
    }
  }

  it('offers no attach control where attachments do not belong', () => {
    renderComposer()

    // Rendered and then refused would be worse than not rendered: a direct-message surface that
    // offers "Attach" and rejects every file teaches people the feature is broken.
    expect(screen.queryByRole('button', { name: 'Attach an image or video' })).not.toBeInTheDocument()
  })

  it('offers it when an upload API is supplied', () => {
    renderComposer({ uploadApi: uploadApi() })

    expect(screen.getByRole('button', { name: 'Attach an image or video' })).toBeInTheDocument()
  })

  it('uploads what a paste carried', () => {
    const api = uploadApi()

    renderComposer({ uploadApi: api })

    const png = new File(['x'], 'shot.png', { type: 'image/png' })

    fireEvent.paste(composer(), {
      clipboardData: { items: [{ kind: 'file', getAsFile: () => png }] },
    })

    // US5's "paste a screenshot". The paste lands on the textarea, which ImageUpload does not own,
    // so the files are handed to it through an imperative handle.
    expect(api.requestUpload).toHaveBeenCalledWith('c1', expect.objectContaining({
      fileName: 'shot.png',
      kind: 'image',
    }) as unknown)
  })

  it('leaves a text-only paste alone', () => {
    const api = uploadApi()

    renderComposer({ uploadApi: api })

    fireEvent.paste(composer(), {
      clipboardData: { items: [{ kind: 'string', getAsFile: () => null }] },
    })

    expect(api.requestUpload).not.toHaveBeenCalled()
  })

  it('will not send an empty message with nothing attached', () => {
    renderComposer({ uploadApi: uploadApi() })

    // A message that is only an attachment IS a message — requiring text would make sharing a
    // screenshot need a caption nobody wants to write — but one with neither is not.
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled()
  })

  it('waits for an upload rather than sending the caption without the picture', async () => {
    const api = {
      requestUpload: vi.fn(() => new Promise<never>(() => undefined)),
    }

    const { enqueued } = renderComposer({ uploadApi: api })

    type('here it is')

    fireEvent.paste(composer(), {
      clipboardData: {
        items: [{ kind: 'file', getAsFile: () => new File(['x'], 'shot.png', { type: 'image/png' }) }],
      },
    })

    // Sending the text now and the attachment when it lands produces two messages for one action,
    // and the reader sees the caption before the picture.
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Uploading…' })).toBeDisabled()
    })

    expect(enqueued).toHaveLength(0)
  })
})
