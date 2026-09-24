/**
 * Tracks the live meeting for one conversation, driven by SignalR (FR-041, FR-047).
 *
 * Its own module rather than an export from `JoinPrompt.tsx`: a file that exports both a component
 * and a hook breaks React Fast Refresh, which this project's lint configuration enforces.
 */

import { useEffect, useState } from 'react'

import type { Meeting } from './JoinPrompt'

/** Subscribes to meeting lifecycle events for one conversation. */
export function useLiveMeeting(
  conversationId: string | null,
  subscribe: (handlers: {
    onStarted: (meetingId: string, conversationId: string, startedBy: string) => void
    onEnded: (meetingId: string, conversationId: string) => void
  }) => () => void,
): Meeting | null {
  const [meeting, setMeeting] = useState<Meeting | null>(null)

  useEffect(() => {
    if (conversationId === null) {
      return
    }

    // setState inside the SUBSCRIPTION CALLBACK, not in the effect body. That is what an effect is
    // for — synchronising with an external system — and is the shape the lint rule permits.
    return subscribe({
      onStarted: (meetingId, startedIn, startedBy) => {
        if (startedIn !== conversationId) {
          return
        }

        setMeeting({
          id: meetingId,
          conversationId: startedIn,
          startedBy,
          startedAt: new Date().toISOString(),
          endedAt: null,

          // Optimistic. The count is corrected the moment the client fetches or joins; showing
          // "0 people" for a meeting somebody just started would read as broken.
          participantCount: 1,
          maxParticipants: 25,
        })
      },
      onEnded: (meetingId, endedIn) => {
        if (endedIn !== conversationId) {
          return
        }

        // Cleared rather than marked ended: nothing renders an ended meeting, and leaving it in
        // state means the prompt has to keep deciding not to show it.
        setMeeting((current) => (current?.id === meetingId ? null : current))
      },
    })
  }, [conversationId, subscribe])

  return meeting
}
