import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { ConversationList } from '../src/features/conversations/ConversationList'
import type { ConversationPage } from '../src/lib/api/messages'
import { aConversation, fakeMessagingClient, renderWithProviders } from './helpers'
import { at, pending } from './at'

/**
 * The conversation list (T100).
 *
 * The component owns no fetching — TanStack Query does — so what is left to assert is the four
 * states it renders and the labelling it applies to each row. The labelling is the part worth the
 * effort: `aria-current` is how a screen-reader user learns which conversation is open, and a
 * direct conversation with no name must never render a raw UUID at somebody.
 */

afterEach(cleanup)

describe('states', () => {
  it('says it is loading before the first page arrives', () => {
    const client = fakeMessagingClient({
      listConversations: vi.fn(() => pending<ConversationPage>()),
    })

    renderWithProviders(
      <ConversationList client={client} selectedId={null} onSelect={() => undefined} />,
    )

    expect(screen.getByText(/loading conversations/i)).toBeInTheDocument()
  })

  it('reports a failure as an alert rather than as an empty list', async () => {
    const client = fakeMessagingClient({
      listConversations: vi.fn(() => Promise.reject(new Error('The API returned 503.'))),
    })

    renderWithProviders(
      <ConversationList client={client} selectedId={null} onSelect={() => undefined} />,
    )

    // An empty list and a failed load look identical, and only one of them means "try again".
    const alert = await screen.findByRole('alert')

    expect(alert).toHaveTextContent(/could not load your conversations/i)
    expect(alert).toHaveTextContent('The API returned 503.')
  })

  it('distinguishes having no conversations from failing to load them', async () => {
    renderWithProviders(
      <ConversationList
        client={fakeMessagingClient()}
        selectedId={null}
        onSelect={() => undefined}
      />,
    )

    expect(await screen.findByTestId('no-conversations')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('rows', () => {
  const conversations = [
    aConversation({ id: 'c1', name: 'Design', unreadCount: 3 }),
    aConversation({ id: 'c2', kind: 'direct', name: null }),
    aConversation({ id: 'c3', kind: 'group', name: null }),
  ]

  function renderList(selectedId: string | null = null, onSelect = vi.fn()) {
    const client = fakeMessagingClient({
      listConversations: vi.fn(() => Promise.resolve({ items: conversations, nextCursor: null })),
    })

    renderWithProviders(
      <ConversationList client={client} selectedId={selectedId} onSelect={onSelect} />,
    )

    return { onSelect }
  }

  it('labels an unnamed conversation without exposing an id', async () => {
    renderList()

    const items = await screen.findAllByTestId('conversation-item')

    expect(at(items, 0)).toHaveTextContent('Design')

    // A direct conversation is named by who is in it, and the participant list arrives with the
    // members endpoint rather than the list projection. "Direct message" is a placeholder; a raw
    // UUID rendered at somebody is a bug they would report.
    expect(at(items, 1)).toHaveTextContent('Direct message')
    expect(at(items, 2)).toHaveTextContent('Untitled conversation')
    expect(at(items, 1).textContent).not.toContain('c2')
  })

  it('marks the open conversation for assistive technology, not only visually', async () => {
    renderList('c2')

    const items = await screen.findAllByTestId('conversation-item')

    // A highlight class alone tells a screen-reader user nothing about which conversation they are
    // in (WCAG 2.1 AA, T215).
    expect(at(items, 1)).toHaveAttribute('aria-current', 'true')
    expect(at(items, 0)).not.toHaveAttribute('aria-current')
  })

  it('gives the unread count an accessible name, and omits it at zero', async () => {
    renderList()

    expect(await screen.findByLabelText('3 unread')).toHaveTextContent('3')

    // A zero badge is noise in every row of a list people scan constantly.
    expect(screen.queryByLabelText('0 unread')).not.toBeInTheDocument()
  })

  it('shows a preview only when there is a last message with a body', async () => {
    const client = fakeMessagingClient({
      listConversations: vi.fn(() =>
        Promise.resolve({
          items: [
            aConversation({
              id: 'c1',
              lastMessage: {
                id: 'm1',
                conversationId: 'c1',
                seq: 9,
                authorId: 'e2',
                clientMessageKey: 'k',
                body: 'shipping tomorrow',
                sentAt: '2026-09-22T09:00:00Z',
                editedAt: null,
                deletedAt: null,
                mentions: [],
                attachments: [],
              },
            }),
            // A conversation whose last message is a tombstone. `body` is null, and rendering it
            // would produce an empty preview row that looks like a layout bug.
            aConversation({
              id: 'c2',
              lastMessage: {
                id: 'm2',
                conversationId: 'c2',
                seq: 4,
                authorId: 'e2',
                clientMessageKey: 'k2',
                body: null,
                sentAt: '2026-09-22T09:00:00Z',
                editedAt: null,
                deletedAt: '2026-09-22T09:01:00Z',
                mentions: [],
                attachments: [],
              },
            }),
          ],
          nextCursor: null,
        }),
      ),
    })

    renderWithProviders(
      <ConversationList client={client} selectedId={null} onSelect={() => undefined} />,
    )

    const previews = await screen.findAllByTestId('conversation-preview')

    expect(previews).toHaveLength(1)
    expect(at(previews, 0)).toHaveTextContent('shipping tomorrow')
  })

  it('selects a conversation when its row is activated', async () => {
    const { onSelect } = renderList()

    const items = await screen.findAllByTestId('conversation-item')

    fireEvent.click(at(items, 1))

    expect(onSelect).toHaveBeenCalledWith('c2')
  })

  it('is reachable by keyboard, because the rows are buttons', async () => {
    const { onSelect } = renderList()

    const items = await screen.findAllByTestId('conversation-item')

    // Buttons rather than clickable list items. A div with onClick takes no focus and fires on no
    // key, which makes the whole list unusable without a mouse. Asserted through focusability and
    // the element's own role rather than by simulating Enter, since jsdom does not synthesise the
    // click a real browser derives from a keypress on a button.
    at(items, 0).focus()
    expect(at(items, 0)).toHaveFocus()
    expect(at(items, 0).tagName).toBe('BUTTON')

    fireEvent.click(at(items, 0))

    await waitFor(() => {
      expect(onSelect).toHaveBeenCalledWith('c1')
    })
  })
})
