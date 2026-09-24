/**
 * T194 — the join prompt shown to a conversation when a meeting starts (FR-041).
 *
 * Deliberately NOT part of the lazy chunk. It has to render the moment the `MeetingStarted`
 * SignalR event arrives, for everyone in the conversation, most of whom will not join — pulling
 * the LiveKit SDK down to show a button would defeat the split entirely. It imports nothing from
 * the meeting room.
 */

import { useCallback } from 'react'

/** A meeting as the API reports it. */
export interface Meeting {
  readonly id: string
  readonly conversationId: string
  readonly startedBy: string
  readonly startedAt: string
  readonly endedAt: string | null
  readonly participantCount: number
  readonly maxParticipants: number
}

interface JoinPromptProps {
  readonly meeting: Meeting | null
  readonly onJoin: (meetingId: string) => void
  /** Whether a join is already in flight, so the button cannot be pressed twice. */
  readonly joining?: boolean | undefined
}

/** The "a meeting is happening" banner. */
export function JoinPrompt({ meeting, onJoin, joining }: JoinPromptProps) {
  // Derived, not stored: the prompt disappears when the meeting ends, and that fact arrives as a
  // prop. Copying it into state would need an effect to re-sync and could disagree.
  const isLive = meeting !== null && meeting.endedAt === null

  const full = meeting !== null && meeting.participantCount >= meeting.maxParticipants

  const join = useCallback(() => {
    if (meeting !== null) {
      onJoin(meeting.id)
    }
  }, [meeting, onJoin])

  if (!isLive) {
    return null
  }

  return (
    <div className="meeting-prompt" role="status" data-testid="meeting-prompt">
      <span>
        A meeting is in progress — {meeting.participantCount} of {meeting.maxParticipants}{' '}
        {meeting.participantCount === 1 ? 'person' : 'people'}
      </span>

      <button
        type="button"
        onClick={join}
        disabled={(joining ?? false) || full}
        data-testid="meeting-join"
      >
        {full ? 'Meeting is full' : 'Join'}
      </button>
    </div>
  )
}
