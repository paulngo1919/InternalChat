import { cleanup, render, screen, fireEvent } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ClipboardEvent } from 'react'

import { imageFilesFromPaste } from '../src/features/attachments/paste'
import { HistoryNotice } from '../src/features/conversations/HistoryNotice'
import {
  conversationsQueryKey,
  historyQueryKey,
  membersQueryKey,
} from '../src/features/conversations/queryKeys'
import { MentionAutocomplete } from '../src/features/messages/MentionAutocomplete'
import { PresenceDot, TypingIndicator } from '../src/features/messages/TypingIndicator'
import { at } from './at'

/**
 * The small presentational pieces, and the query keys they are cached under.
 *
 * Each of these is a handful of lines, and the temptation is to treat them as too simple to test.
 * The accessibility assertions are why they are not: presence is conveyed by a coloured dot, and
 * colour alone fails WCAG 2.1 AA 1.4.1 — a defect that is invisible to everyone who can see the
 * colour, which includes everyone who would ever notice it by looking.
 */

afterEach(cleanup)

describe('query keys', () => {
  it('scopes history and members per conversation', () => {
    expect(conversationsQueryKey).toEqual(['conversations'])

    // Keyed by id, so switching conversations does not refetch both — and, more importantly, so
    // invalidating one conversation's history cannot drop another's.
    expect(historyQueryKey('c1')).toEqual(['conversations', 'c1', 'messages'])
    expect(membersQueryKey('c1')).toEqual(['conversations', 'c1', 'members'])
    expect(historyQueryKey('c1')).not.toEqual(historyQueryKey('c2'))
  })

  it('nests under the list key so invalidating the list reaches them', () => {
    // TanStack Query matches by key prefix. These sharing the 'conversations' head is what lets a
    // membership change invalidate everything about a conversation in one call.
    expect(at(historyQueryKey('c1'), 0)).toBe(at(conversationsQueryKey, 0))
    expect(at(membersQueryKey('c1'), 0)).toBe(at(conversationsQueryKey, 0))
  })
})

describe('HistoryNotice', () => {
  it('states plainly what a new member can see', () => {
    render(<HistoryNotice historyVisibility="full" />)

    expect(screen.getByRole('note')).toHaveTextContent(/full history/i)

    cleanup()
    render(<HistoryNotice historyVisibility="from_join" />)

    // US3 scenario 4. A member who cannot see this rule has no way to know whether an old message
    // they cannot find was deleted or was simply never visible to them.
    expect(screen.getByRole('note')).toHaveTextContent(/only see messages sent after they joined/i)
  })
})

describe('TypingIndicator', () => {
  it('renders nothing when nobody is typing', () => {
    render(<TypingIndicator typing={[]} names={{}} />)

    // Nothing, not an empty reserved row. A row that is always present but usually blank is a
    // visible gap people ask about.
    expect(screen.queryByTestId('typing-indicator')).not.toBeInTheDocument()
  })

  it('names one and two typists', () => {
    const names = { e1: 'An', e2: 'Bình', e3: 'Chi' }

    render(<TypingIndicator typing={['e1']} names={names} />)
    expect(screen.getByTestId('typing-indicator')).toHaveTextContent('An is typing…')

    cleanup()
    render(<TypingIndicator typing={['e1', 'e2']} names={names} />)
    expect(screen.getByTestId('typing-indicator')).toHaveTextContent('An and Bình are typing…')
  })

  it('counts beyond two rather than naming everyone', () => {
    render(
      <TypingIndicator
        typing={['e1', 'e2', 'e3']}
        names={{ e1: 'An', e2: 'Bình', e3: 'Chi' }}
      />,
    )

    // Naming everyone makes the line jump in width on every keystroke and pushes the transcript
    // around under the reader's eye. A count does not.
    expect(screen.getByTestId('typing-indicator')).toHaveTextContent('3 people are typing…')
  })

  it('falls back to "Someone" for an id it has no name for', () => {
    // A member who joined during this session, before the member list was refetched. The indicator
    // is ephemeral by contract and must not wait for a name it may never get.
    render(<TypingIndicator typing={['unknown']} names={{}} />)
    expect(screen.getByTestId('typing-indicator')).toHaveTextContent('Someone is typing…')

    cleanup()
    render(<TypingIndicator typing={['unknown', 'other']} names={{}} />)

    // Both capitalised. The lowercase `?? 'someone'` inside the two-typist template is unreachable:
    // `labels` is built by `map`, so its entries are always strings and the fallback has already
    // been applied. It is there to satisfy `noUncheckedIndexedAccess`, not to run.
    expect(screen.getByTestId('typing-indicator')).toHaveTextContent('Someone and Someone are typing…')
  })

  it('announces politely rather than interrupting', () => {
    render(<TypingIndicator typing={['e1']} names={{ e1: 'An' }} />)

    // `assertive` would interrupt the message someone is mid-way through reading to tell them a
    // colleague started typing.
    expect(screen.getByRole('status')).toHaveAttribute('aria-live', 'polite')
  })
})

describe('PresenceDot', () => {
  it.each([
    ['online', 'An is online'],
    ['away', 'An is away'],
    ['offline', 'An is offline'],
    ['dnd', 'An is do not disturb'],
  ] as const)('puts %s in the accessible name, not only the colour', (presence, label) => {
    render(<PresenceDot presence={presence} name="An" />)

    // Colour alone fails WCAG 2.1 AA (1.4.1) and is invisible to roughly one in twelve men. On an
    // internal tool that is a real fraction of the company, and the failure is undetectable by
    // anyone who can see the colour.
    expect(screen.getByRole('img')).toHaveAccessibleName(label)
    expect(screen.getByTestId('presence')).toHaveAttribute('data-presence', presence)
  })
})

describe('MentionAutocomplete', () => {
  const candidates = [
    { id: 'e1', displayName: 'An Nguyen' },
    { id: 'e2', displayName: 'Bình Tran' },
  ]

  it('renders nothing when no candidate matches', () => {
    render(<MentionAutocomplete query="zzz" candidates={candidates} onSelect={() => undefined} />)

    expect(screen.queryByTestId('mention-autocomplete')).not.toBeInTheDocument()
  })

  it('offers the matching members as options', () => {
    render(<MentionAutocomplete query="an" candidates={candidates} onSelect={() => undefined} />)

    expect(screen.getByRole('listbox')).toHaveAccessibleName('Mention a colleague')

    const options = screen.getAllByTestId('mention-option')

    expect(options.map((option) => option.textContent)).toContain('An Nguyen')
  })

  it('selects on mouse-down rather than click', () => {
    const onSelect = vi.fn()

    render(<MentionAutocomplete query="an" candidates={candidates} onSelect={onSelect} />)

    const option = at(screen.getAllByTestId('mention-option'), 0)

    // A click fires after the textarea's blur, by which point the caret position the insertion
    // depends on has already moved — the mention would land in the wrong place, or at the end.
    fireEvent.mouseDown(option)

    expect(onSelect).toHaveBeenCalledWith({ id: 'e1', displayName: 'An Nguyen' })

    onSelect.mockClear()
    fireEvent.click(option)

    expect(onSelect).not.toHaveBeenCalled()
  })

  it('offers only the conversation members it was given', () => {
    // FR-015 resolves mentions against active members at send time. Offering anyone else lets
    // someone pick a name the server discards silently — a UI promise the API never keeps.
    render(<MentionAutocomplete query="" candidates={[]} onSelect={() => undefined} />)

    expect(screen.queryByTestId('mention-autocomplete')).not.toBeInTheDocument()
  })
})

describe('paste', () => {
  /** A clipboard event carrying the given items. */
  function pasteOf(items: { kind: string; file: File | null }[]): ClipboardEvent {
    return {
      clipboardData: {
        items: items.map((item) => ({ kind: item.kind, getAsFile: () => item.file })),
      },
    } as unknown as ClipboardEvent
  }

  it('returns the attachable images a paste carried', () => {
    const png = new File([''], 'shot.png', { type: 'image/png' })

    const files = imageFilesFromPaste(pasteOf([{ kind: 'file', file: png }]))

    // Returned rather than consumed: a paste carrying both text and an image should still paste the
    // text, and only the caller knows whether its input had a selection worth replacing.
    expect(files).toEqual([png])
  })

  it('ignores the text representations a paste also carries', () => {
    const png = new File([''], 'shot.png', { type: 'image/png' })

    const files = imageFilesFromPaste(
      pasteOf([
        { kind: 'string', file: null },
        { kind: 'file', file: png },
      ]),
    )

    expect(files).toEqual([png])
  })

  it('drops files of a type neither allow-list accepts, without complaining', () => {
    const files = imageFilesFromPaste(
      pasteOf([
        { kind: 'file', file: new File([''], 'notes.pdf', { type: 'application/pdf' }) },
        { kind: 'file', file: new File([''], 'chart.svg', { type: 'image/svg+xml' }) },
      ]),
    )

    // Pasting from a spreadsheet or a design tool routinely puts several representations on the
    // clipboard. Complaining about the ones we cannot use would make an ordinary paste look broken.
    // (SVG is excluded deliberately — it is a script-bearing document, not an image.)
    expect(files).toEqual([])
  })

  it('tolerates an item that yields no file', () => {
    // getAsFile can return null for an item that claims to be a file, which happens with some
    // clipboard sources. Dereferencing it would throw inside a paste handler.
    const files = imageFilesFromPaste(pasteOf([{ kind: 'file', file: null }]))

    expect(files).toEqual([])
  })
})
