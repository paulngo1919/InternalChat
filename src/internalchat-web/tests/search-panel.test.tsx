import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { SearchPanel, type SearchApi } from '../src/features/search/SearchPanel'
import type { SearchResult, SearchResultPage } from '../src/lib/api/messages'
import { pending } from './at'

/**
 * Search (T168 — FR-029 to FR-033).
 *
 * Three things here are correctness rather than polish, and each has its own section below.
 *
 * <b>`truncated` must be rendered.</b> FR-033. A truncated result set and an exhaustive one look
 * identical, so a panel that ignores the flag tells the person "that is everything" when it is not.
 * This is the only place in the client where saying nothing is a correctness bug.
 *
 * <b>Responses must be applied in order.</b> A slow search for "de" can land after a fast one for
 * "deployment" and overwrite the better results with worse ones — which looks exactly like the
 * search ignoring what was typed, and is invisible on a fast local connection.
 *
 * <b>The snippet is HTML from the server.</b> `ts_headline` wraps matches in `<mark>`, which means
 * `dangerouslySetInnerHTML`. `tests/search-highlight.test.ts` covers the sanitizer itself; what is
 * asserted here is that the sanitizer is actually the thing standing between a message body and the
 * DOM.
 */

function aResult(overrides: Partial<SearchResult> = {}): SearchResult {
  return {
    messageId: 'm1',
    conversationId: 'c1',
    conversationName: 'Design',
    seq: 42,
    highlight: 'the <mark>deploy</mark> is tomorrow',
    rank: 0.9,
    ...overrides,
  }
}

function aPage(overrides: Partial<SearchResultPage> = {}): SearchResultPage {
  return { items: [aResult()], nextCursor: null, truncated: false, ...overrides }
}

function fakeApi(overrides: Partial<SearchApi> = {}): SearchApi {
  return { searchMessages: vi.fn(() => Promise.resolve(aPage())), ...overrides }
}

function renderPanel(overrides: Partial<Parameters<typeof SearchPanel>[0]> = {}) {
  const api = overrides.api ?? fakeApi()
  const onJumpTo = vi.fn()

  render(<SearchPanel api={api} onJumpTo={onJumpTo} {...overrides} />)

  return { api, onJumpTo }
}

/** Types into the search box and lets the debounce elapse. */
async function search(text: string) {
  fireEvent.change(screen.getByTestId('search-input'), { target: { value: text } })

  await act(async () => {
    vi.advanceTimersByTime(300)
  })
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
})

afterEach(() => {
  vi.useRealTimers()
  cleanup()
})

describe('querying', () => {
  it('does not search for a query shorter than the server minimum', async () => {
    const { api } = renderPanel()

    await search('d')

    // Matches the server's MinimumQueryLength. A one-character query matches most of a corpus and
    // the server refuses it anyway.
    expect(api.searchMessages).not.toHaveBeenCalled()
  })

  it('debounces, so typing a word is one request rather than eight', async () => {
    const { api } = renderPanel()

    const input = screen.getByTestId('search-input')

    for (const text of ['de', 'dep', 'depl', 'deplo', 'deploy']) {
      fireEvent.change(input, { target: { value: text } })

      await act(async () => {
        vi.advanceTimersByTime(100)
      })
    }

    await act(async () => {
      vi.advanceTimersByTime(300)
    })

    // The server rate-limits search with a fixed window. A request per keystroke exhausts it
    // mid-word and leaves the person looking at a 429 for typing normally.
    await waitFor(() => {
      expect(api.searchMessages).toHaveBeenCalledTimes(1)
    })

    expect(api.searchMessages).toHaveBeenCalledWith('deploy', {})
  })

  it('trims before searching and before measuring length', async () => {
    const { api } = renderPanel()

    await search('  deploy  ')

    await waitFor(() => {
      expect(api.searchMessages).toHaveBeenCalledWith('deploy', {})
    })
  })

  it('runs immediately on submit rather than waiting out the debounce', async () => {
    const { api } = renderPanel()

    fireEvent.change(screen.getByTestId('search-input'), { target: { value: 'deploy' } })
    fireEvent.submit(screen.getByTestId('search-input').closest('form') as HTMLFormElement)

    await waitFor(() => {
      expect(api.searchMessages).toHaveBeenCalledTimes(1)
    })
  })

  it('ignores a submit of a too-short query', async () => {
    const { api } = renderPanel()

    fireEvent.change(screen.getByTestId('search-input'), { target: { value: 'd' } })
    fireEvent.submit(screen.getByTestId('search-input').closest('form') as HTMLFormElement)

    expect(api.searchMessages).not.toHaveBeenCalled()
  })

  it('hides previous results once the query becomes too short again', async () => {
    renderPanel()

    await search('deploy')

    expect(await screen.findByTestId('search-results')).toBeInTheDocument()

    fireEvent.change(screen.getByTestId('search-input'), { target: { value: 'd' } })

    // Derived from the query rather than cleared in an effect — a second source of truth for the
    // same fact is what leaves a stale result list under a cleared search box.
    expect(screen.queryByTestId('search-results')).not.toBeInTheDocument()
  })
})

describe('filters', () => {
  it('sends only the filters that were set', async () => {
    const { api } = renderPanel()

    fireEvent.change(screen.getByLabelText('Attachments'), { target: { value: 'image' } })
    fireEvent.change(screen.getByLabelText('From'), { target: { value: '2026-01-01' } })

    await search('deploy')

    await waitFor(() => {
      // No empty `to`. The server would read one as a filter rather than as its absence.
      expect(api.searchMessages).toHaveBeenLastCalledWith('deploy', {
        hasAttachment: 'image',
        from: '2026-01-01',
      })
    })
  })

  it('scopes to one conversation when opened from inside one', async () => {
    const { api } = renderPanel({ conversationId: 'c9' })

    await search('deploy')

    await waitFor(() => {
      expect(api.searchMessages).toHaveBeenCalledWith('deploy', { conversationId: 'c9' })
    })
  })

  it('re-runs when a filter changes', async () => {
    const { api } = renderPanel()

    await search('deploy')

    await waitFor(() => {
      expect(api.searchMessages).toHaveBeenCalledTimes(1)
    })

    fireEvent.change(screen.getByLabelText('Attachments'), { target: { value: 'video' } })

    await act(async () => {
      vi.advanceTimersByTime(300)
    })

    // Changing a filter without re-running would leave results on screen that do not match the
    // filters shown above them.
    await waitFor(() => {
      expect(api.searchMessages).toHaveBeenLastCalledWith('deploy', { hasAttachment: 'video' })
    })
  })

  it('labels every filter control', () => {
    renderPanel()

    // The section and the input share the name "Search messages", so this asks for the control
    // specifically rather than for anything carrying that label.
    expect(screen.getByRole('searchbox', { name: 'Search messages' })).toBe(
      screen.getByTestId('search-input'),
    )
    expect(screen.getByLabelText('Attachments')).toBeInTheDocument()
    expect(screen.getByLabelText('From')).toBeInTheDocument()
    expect(screen.getByLabelText('To')).toBeInTheDocument()
  })
})

describe('out-of-order responses', () => {
  it('ignores a slow response that a newer search has superseded', async () => {
    let releaseSlow: ((page: SearchResultPage) => void) | null = null

    const searchMessages = vi
      .fn()
      .mockImplementationOnce(
        () =>
          new Promise<SearchResultPage>((resolve) => {
            releaseSlow = resolve
          }),
      )
      .mockResolvedValue(aPage({ items: [aResult({ messageId: 'm2', highlight: 'newer' })] }))

    renderPanel({ api: { searchMessages } })

    await search('de')
    await search('deployment')

    await waitFor(() => {
      expect(screen.getByTestId('search-results')).toHaveTextContent('newer')
    })

    // The stale response lands last and must be discarded. Applying it would replace the results
    // for "deployment" with the ones for "de" — which reads as the search ignoring what was typed.
    act(() => {
      releaseSlow?.(aPage({ items: [aResult({ messageId: 'm3', highlight: 'stale' })] }))
    })

    await waitFor(() => {
      expect(screen.getByTestId('search-results')).toHaveTextContent('newer')
    })

    expect(screen.getByTestId('search-results')).not.toHaveTextContent('stale')
  })

  it('ignores a stale failure too', async () => {
    let rejectSlow: ((error: Error) => void) | null = null

    const searchMessages = vi
      .fn()
      .mockImplementationOnce(
        () =>
          new Promise<SearchResultPage>((_, reject) => {
            rejectSlow = reject
          }),
      )
      .mockResolvedValue(aPage())

    renderPanel({ api: { searchMessages } })

    await search('de')
    await search('deployment')

    await waitFor(() => {
      expect(screen.getByTestId('search-results')).toBeInTheDocument()
    })

    act(() => {
      rejectSlow?.(new Error('timeout'))
    })

    // A superseded failure must not put an error over results that are perfectly good.
    await waitFor(() => {
      expect(screen.queryByRole('alert')).not.toBeInTheDocument()
    })
  })
})

describe('results', () => {
  it('renders the truncation notice when the search was cut short', async () => {
    renderPanel({ api: fakeApi({ searchMessages: vi.fn(() => Promise.resolve(aPage({ truncated: true }))) }) })

    await search('deploy')

    // FR-033. Not a warning about an error — the results shown are real — but the person must not
    // read "nothing further" into a list that was cut short.
    const notice = await screen.findByTestId('search-truncated')

    expect(notice).toHaveTextContent(/partial results/i)
    expect(notice).toHaveTextContent(/narrowing the date range/i)
  })

  it('says nothing about truncation for a complete search', async () => {
    renderPanel()

    await search('deploy')

    await screen.findByTestId('search-results')

    expect(screen.queryByTestId('search-truncated')).not.toBeInTheDocument()
  })

  it('distinguishes no matches from a search still running', async () => {
    renderPanel({ api: fakeApi({ searchMessages: vi.fn(() => Promise.resolve(aPage({ items: [] }))) }) })

    await search('deploy')

    expect(await screen.findByText('No messages match that search.')).toBeInTheDocument()
  })

  it('announces that a search is running', async () => {
    renderPanel({ api: fakeApi({ searchMessages: vi.fn(() => pending<SearchResultPage>()) }) })

    await search('deploy')

    // A polite live region, so a screen reader mentions it without interrupting.
    const status = screen.getByRole('status')

    expect(status).toHaveTextContent('Searching…')
    expect(status).toHaveAttribute('aria-live', 'polite')
  })

  it('reports a failed search as an alert', async () => {
    renderPanel({ api: fakeApi({ searchMessages: vi.fn(() => Promise.reject(new Error('503'))) }) })

    await search('deploy')

    expect(await screen.findByRole('alert')).toHaveTextContent('could not be completed')
  })

  it('jumps to the message in its conversation', async () => {
    const { onJumpTo } = renderPanel()

    await search('deploy')

    fireEvent.click(await screen.findByRole('button'))

    // FR-031 navigates by sequence, which is the message's stable position in its conversation.
    expect(onJumpTo).toHaveBeenCalledWith('c1', 42)
  })

  it('labels a result from an unnamed conversation', async () => {
    renderPanel({
      api: fakeApi({
        searchMessages: vi.fn(() =>
          Promise.resolve(aPage({ items: [aResult({ conversationName: null })] })),
        ),
      }),
    })

    await search('deploy')

    expect(await screen.findByText('Direct message')).toBeInTheDocument()
  })
})

describe('the highlight', () => {
  it('renders the server mark elements', async () => {
    renderPanel()

    await search('deploy')

    const results = await screen.findByTestId('search-results')

    expect(results.querySelector('mark')).toHaveTextContent('deploy')
  })

  it('escapes everything except the mark markers', async () => {
    renderPanel({
      api: fakeApi({
        searchMessages: vi.fn(() =>
          Promise.resolve(
            aPage({
              items: [
                aResult({
                  // A message body someone actually typed. Rendered raw this executes; rendered
                  // through the sanitizer it is displayed as text.
                  highlight: '<img src=x onerror=alert(1)> and <mark>deploy</mark>',
                }),
              ],
            }),
          ),
        ),
      }),
    })

    await search('deploy')

    const results = await screen.findByTestId('search-results')

    expect(results.querySelector('img')).toBeNull()
    expect(results.textContent).toContain('<img src=x onerror=alert(1)>')

    // The one element on the allow-list survives. It takes no attributes, no URL and no behaviour,
    // so a forged one can highlight a word and nothing else.
    expect(results.querySelector('mark')).toHaveTextContent('deploy')
  })
})
