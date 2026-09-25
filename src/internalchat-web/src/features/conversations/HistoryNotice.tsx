/**
 * T121 — displays the active history-visibility rule to members (US3 scenario 4, FR-008).
 *
 * The rule is fixed at creation (`Conversation.HistoryVisibility` has no setter — see the domain
 * type's remarks) and is exactly what decides whether a newly added member can scroll back to
 * before they joined. Showing it plainly is what US3 scenario 4 means by "the rule is displayed to
 * them" — a member who cannot see it has no way to know whether an old message they cannot find was
 * deleted or was simply never visible to them.
 */

import { Info } from 'lucide-react'

interface HistoryNoticeProps {
  readonly historyVisibility: 'from_join' | 'full'
}

/** A one-line, plain-language statement of what a member of this conversation can see. */
export function HistoryNotice({ historyVisibility }: HistoryNoticeProps) {
  return (
    <div role="note" data-testid="history-notice" className="chat-notice-banner">
      <Info size={16} className="shrink-0" />
      <p>
        {historyVisibility === 'full'
          ? 'New members can see the full history of this conversation, including messages sent before they joined.'
          : 'New members only see messages sent after they joined this conversation.'}
      </p>
    </div>
  )
}
