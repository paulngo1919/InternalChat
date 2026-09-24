/**
 * T168 — search with filters and jump-to-context (FR-029 – FR-033).
 *
 * Two things here are requirements rather than polish:
 *
 * 1. **`truncated` is rendered.** FR-033. A truncated result set and an exhaustive one look
 *    identical, so a UI that ignores the flag tells the person "that is everything" when it is not.
 *    This is the only place in the client where saying nothing would be a correctness bug rather
 *    than a missing feature.
 * 2. **The highlight is server-generated and rendered as HTML.** `ts_headline` wraps matches in
 *    `<mark>`. Rendering it means `dangerouslySetInnerHTML`, which is only acceptable because the
 *    snippet is escaped before the markers are inserted — see `renderHighlight`.
 */

import { useCallback, useEffect, useRef, useState, type SyntheticEvent } from 'react'

import type { SearchResult, SearchResultPage } from '../../lib/api/messages'
import { renderHighlight } from './highlight'

/** What the panel needs from the API client. */
export interface SearchApi {
  searchMessages(
    q: string,
    filters?: {
      conversationId?: string
      authorId?: string
      from?: string
      to?: string
      hasAttachment?: 'any' | 'image' | 'video'
      cursor?: string
      limit?: number
    },
  ): Promise<SearchResultPage>
}

interface SearchPanelProps {
  readonly api: SearchApi
  /** Navigates to a message in its conversation (FR-031). */
  readonly onJumpTo: (conversationId: string, seq: number) => void
  /** Restricts the search to one conversation, when opened from inside one. */
  readonly conversationId?: string | undefined
}

/** Matches the server's `MinimumQueryLength`. */
const MINIMUM_QUERY_LENGTH = 2

/** How long after the last keystroke the search runs. */
const DEBOUNCE_MS = 300

/** The search panel. */
export function SearchPanel({ api, onJumpTo, conversationId }: SearchPanelProps) {
  const [query, setQuery] = useState('')
  const [hasAttachment, setHasAttachment] = useState<'' | 'any' | 'image' | 'video'>('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')

  const [page, setPage] = useState<SearchResultPage | null>(null)
  const [status, setStatus] = useState<'idle' | 'searching' | 'failed'>('idle')

  // Identifies the newest in-flight request. Without it a slow search for "de" can land after a
  // fast one for "deployment" and overwrite the better results with worse ones — the classic
  // out-of-order response bug, which looks like the search ignoring what you typed.
  const generation = useRef(0)

  const run = useCallback(
    async (text: string) => {
      const mine = ++generation.current

      setStatus('searching')

      try {
        const result = await api.searchMessages(text, {
          ...(conversationId !== undefined ? { conversationId } : {}),
          ...(hasAttachment !== '' ? { hasAttachment } : {}),
          ...(from !== '' ? { from } : {}),
          ...(to !== '' ? { to } : {}),
        })

        if (mine === generation.current) {
          setPage(result)
          setStatus('idle')
        }
      } catch {
        if (mine === generation.current) {
          setStatus('failed')
        }
      }
    },
    [api, conversationId, hasAttachment, from, to],
  )

  // Debounced, because the server rate-limits search with a fixed window: a request per keystroke
  // would exhaust it mid-word and leave the person looking at a 429 for typing normally.
  useEffect(() => {
    const trimmed = query.trim()

    if (trimmed.length < MINIMUM_QUERY_LENGTH) {
      // Nothing to clear. `showResults` below derives visibility from the query itself, so a
      // too-short query hides the previous page without a state write — which keeps this effect
      // free of the cascading render that setting state inside one causes.
      return
    }

    const timer = setTimeout(() => {
      void run(trimmed)
    }, DEBOUNCE_MS)

    return () => {
      clearTimeout(timer)
    }
  }, [query, run])

  const onSubmit = useCallback(
    (event: SyntheticEvent) => {
      // Submitting runs the search immediately rather than waiting out the debounce.
      event.preventDefault()

      const trimmed = query.trim()

      if (trimmed.length >= MINIMUM_QUERY_LENGTH) {
        void run(trimmed)
      }
    },
    [query, run],
  )

  // Derived rather than stored. Clearing `page` in an effect when the query gets too short would
  // be a second source of truth for the same fact.
  const showResults = query.trim().length >= MINIMUM_QUERY_LENGTH

  return (
    <section className="search" aria-label="Search messages">
      <form onSubmit={onSubmit}>
        <label htmlFor="search-q">Search messages</label>
        <input
          id="search-q"
          type="search"
          value={query}
          onChange={(event) => {
            setQuery(event.target.value)
          }}
          placeholder="Search your conversations"
          data-testid="search-input"
        />

        <fieldset className="search__filters">
          <legend>Filters</legend>

          <label htmlFor="search-attachment">Attachments</label>
          <select
            id="search-attachment"
            value={hasAttachment}
            onChange={(event) => {
              setHasAttachment(event.target.value as '' | 'any' | 'image' | 'video')
            }}
          >
            <option value="">Any message</option>
            <option value="any">With an attachment</option>
            <option value="image">With an image</option>
            <option value="video">With a video</option>
          </select>

          <label htmlFor="search-from">From</label>
          <input
            id="search-from"
            type="date"
            value={from}
            onChange={(event) => {
              setFrom(event.target.value)
            }}
          />

          <label htmlFor="search-to">To</label>
          <input
            id="search-to"
            type="date"
            value={to}
            onChange={(event) => {
              setTo(event.target.value)
            }}
          />
        </fieldset>
      </form>

      <div role="status" aria-live="polite">
        {status === 'searching' && <span>Searching…</span>}
        {status === 'failed' && <span role="alert">The search could not be completed.</span>}
      </div>

      {showResults && page !== null && (
        <>
          {page.truncated && (
            // FR-033. Not a warning about an error — the results shown are real — but the person
            // must not read "nothing further" into a list that was cut short.
            <p className="search__truncated" data-testid="search-truncated">
              Showing partial results — the search was cut short. Narrowing the date range or adding
              a filter will make it complete.
            </p>
          )}

          {page.items.length === 0 && status === 'idle' && (
            <p className="search__empty">No messages match that search.</p>
          )}

          <ul className="search__results" data-testid="search-results">
            {page.items.map((result) => (
              <li key={result.messageId}>
                <ResultRow result={result} onJumpTo={onJumpTo} />
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  )
}

function ResultRow({
  result,
  onJumpTo,
}: {
  readonly result: SearchResult
  readonly onJumpTo: (conversationId: string, seq: number) => void
}) {
  return (
    <button
      type="button"
      className="search__result"
      onClick={() => {
        onJumpTo(result.conversationId, result.seq)
      }}
    >
      <span className="search__conversation">
        {result.conversationName ?? 'Direct message'}
      </span>

      {/*
        The snippet is the only field carrying message text, and the only place this app renders
        HTML it did not build. renderHighlight escapes the text before re-inserting the <mark>
        markers, so a message containing "<script>" is displayed, never executed.
      */}
      {/*
        Sanitizer: renderHighlight in src/features/search/highlight.ts. The snippet is fully
        HTML-escaped first, then exactly one element is restored from its escaped form: <mark>.
        That is the entire allow-list. <mark> takes no attributes, no URL and no behaviour, so a
        forged one can highlight a word and nothing else.
      */}
      <span
        className="search__highlight"
        // eslint-disable-next-line no-restricted-syntax
        dangerouslySetInnerHTML={{ __html: renderHighlight(result.highlight) }}
      />
    </button>
  )
}
