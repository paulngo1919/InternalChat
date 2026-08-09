import { useCallback, useEffect, useMemo, useState } from 'react'

import { createApiClient, type CurrentEmployee, type Session } from './lib/api/client'
import { readApiBaseUrl } from './lib/auth/config'
import { useAuth } from './lib/auth/authContext'
import './App.css'

/**
 * US1's visible surface: who you are, and which devices are signed in as you (FR-005).
 *
 * Conversations arrive with US2. What is here is what US1 actually delivers, and it is what
 * quickstart V1 walks through — reaching a signed-in view without ever creating a chat password.
 */
export default function App() {
  const { getAccessToken, signOut } = useAuth()

  const api = useMemo(
    () => createApiClient(readApiBaseUrl(import.meta.env), getAccessToken),
    [getAccessToken],
  )

  const [me, setMe] = useState<CurrentEmployee | null>(null)
  const [sessions, setSessions] = useState<Session[]>([])
  const [error, setError] = useState<string | null>(null)

  const loadSessions = useCallback(async () => {
    setSessions(await api.getSessions())
  }, [api])

  useEffect(() => {
    let cancelled = false

    async function load(): Promise<void> {
      try {
        const profile = await api.getMe()

        if (!cancelled) {
          setMe(profile)
          await loadSessions()
        }
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof Error ? cause.message : 'Could not load your profile.')
        }
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [api, loadSessions])

  const revoke = useCallback(
    async (sessionId: string) => {
      await api.revokeSession(sessionId)
      await loadSessions()
    },
    [api, loadSessions],
  )

  if (error) {
    return (
      <main>
        <p role="alert">{error}</p>
      </main>
    )
  }

  if (!me) {
    return (
      <main>
        <p>Loading your profile…</p>
      </main>
    )
  }

  return (
    <main>
      <header>
        <h1 data-testid="display-name">{me.displayName}</h1>
        <p>{me.email}</p>
        <button type="button" onClick={signOut}>
          Sign out
        </button>
      </header>

      {/*
        FR-040: say plainly when this browser cannot be reached, rather than letting an employee
        assume a message will find them. Push subscriptions arrive with US4; until then the honest
        answer is that nobody is reachable, and that is what this shows.
      */}
      {!me.canReceiveNotifications && (
        <p role="status" data-testid="notification-warning">
          Notifications are off for this browser, so you will not be alerted to new messages while
          this tab is in the background.
        </p>
      )}

      <section aria-labelledby="sessions-heading">
        <h2 id="sessions-heading">Your signed-in devices</h2>

        <ul>
          {sessions.map((session) => (
            <li key={session.id} data-testid="session">
              <span>{session.userAgent || 'Unknown device'}</span>
              {session.isCurrent && <span> (this device)</span>}
              <button
                type="button"
                onClick={() => {
                  void revoke(session.id)
                }}
              >
                Sign this device out
              </button>
            </li>
          ))}
        </ul>
      </section>
    </main>
  )
}
