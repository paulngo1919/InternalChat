import './loading.css'

/**
 * Placeholders shaped like the content they stand in for, so the layout does not jump when it
 * arrives. Every one carries a visually hidden, polite status line: the shimmer means nothing to a
 * screen reader, and it is the only thing that tells someone with reduced motion that work is going on.
 */

/** Stands in for the conversation list: avatar, title, and a line of preview per row. */
export function ConversationListSkeleton({ rows = 6 }: { readonly rows?: number }) {
  return (
    <div className="skeleton-list">
      <span className="visually-hidden" role="status" aria-live="polite">
        Loading conversations…
      </span>
      {Array.from({ length: rows }, (_, index) => (
        <div
          key={index}
          className="skeleton-row"
          aria-hidden="true"
          style={{ animationDelay: `${String(index * 60)}ms` }}
        >
          <span className="skeleton skeleton--circle" />
          <span className="skeleton-row__lines">
            <span className="skeleton skeleton--line" style={{ width: `${String(55 + ((index * 17) % 30))}%` }} />
            <span className="skeleton skeleton--line skeleton--thin" style={{ width: `${String(70 + ((index * 23) % 25))}%` }} />
          </span>
        </div>
      ))}
    </div>
  )
}

/** Alternating bubbles of varied width, mine on the right, theirs on the left. */
const BUBBLES: readonly { readonly mine: boolean; readonly width: number }[] = [
  { mine: false, width: 46 },
  { mine: false, width: 28 },
  { mine: true, width: 38 },
  { mine: false, width: 58 },
  { mine: true, width: 24 },
  { mine: true, width: 44 },
  { mine: false, width: 34 },
]

/** Stands in for the transcript while the first page of history is on its way. */
export function MessageListSkeleton() {
  return (
    <div className="skeleton-transcript">
      <span className="visually-hidden" role="status" aria-live="polite">
        Loading messages…
      </span>
      {BUBBLES.map((bubble, index) => (
        <span
          key={index}
          aria-hidden="true"
          className={`skeleton skeleton--bubble${bubble.mine ? ' skeleton--mine' : ''}`}
          style={{ width: `${String(bubble.width)}%`, animationDelay: `${String(index * 70)}ms` }}
        />
      ))}
    </div>
  )
}

/** A small inline wait for secondary panels (settings, members). The label is what gets announced. */
export function Spinner({ label }: { readonly label: string }) {
  return (
    <span className="inline-loading" role="status" aria-live="polite">
      <span className="spinner" aria-hidden="true" />
      <span>{label}</span>
    </span>
  )
}
