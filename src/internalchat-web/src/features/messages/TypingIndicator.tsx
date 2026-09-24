/**
 * T104 — typing indicators and presence.
 *
 * Both are ephemeral by contract: the server holds them in Redis with a TTL and never cleans them up
 * explicitly, so a crashed client stops appearing to type on its own (contracts/signalr-hub.md). The
 * client mirrors that — it renders whatever the last event said and does not try to reason about who
 * ought still to be typing.
 */

/** `PresenceChanged` values, from openapi.yaml's `EmployeeSummary.presence`. */
export type Presence = 'online' | 'away' | 'offline' | 'dnd'

interface TypingIndicatorProps {
  /** Employee ids currently typing, excluding the reader. */
  readonly typing: readonly string[]
  /** Display names by employee id, as far as they are known. */
  readonly names: Readonly<Record<string, string>>
}

/** Names the typists, or counts them once there are too many to name. */
function describe(typing: readonly string[], names: Readonly<Record<string, string>>): string {
  const labels = typing.map((id) => names[id] ?? 'Someone')

  if (labels.length === 1) {
    return `${labels[0] ?? 'Someone'} is typing…`
  }

  if (labels.length === 2) {
    return `${labels[0] ?? 'Someone'} and ${labels[1] ?? 'someone'} are typing…`
  }

  // Beyond two, naming everyone makes the line jump in width on every keystroke and pushes the
  // transcript around. A count does not.
  return `${String(labels.length)} people are typing…`
}

/** Who is typing, or nothing at all. */
export function TypingIndicator({ typing, names }: TypingIndicatorProps) {
  if (typing.length === 0) {
    // Rendered as nothing rather than as an empty reserved row. A row that is always present but
    // usually blank is a visible gap people ask about.
    return null
  }

  return (
    // `polite`, not `assertive`: a screen reader should mention this between utterances, not
    // interrupt the message someone is reading to announce that a colleague is typing.
    <p role="status" aria-live="polite" data-testid="typing-indicator">
      {describe(typing, names)}
    </p>
  )
}

interface PresenceDotProps {
  readonly presence: Presence
  readonly name: string
}

/** Someone's availability (FR-017). */
export function PresenceDot({ presence, name }: PresenceDotProps) {
  const label: Record<Presence, string> = {
    online: 'online',
    away: 'away',
    offline: 'offline',
    dnd: 'do not disturb',
  }

  return (
    // The state is in the accessible name, not only in a colour. Colour alone fails WCAG 2.1 AA
    // (1.4.1 Use of Colour) and is invisible to the roughly one in twelve men with a colour-vision
    // deficiency — on an internal tool, that is a real fraction of the company.
    <span
      data-testid="presence"
      data-presence={presence}
      role="img"
      aria-label={`${name} is ${label[presence]}`}
    />
  )
}
