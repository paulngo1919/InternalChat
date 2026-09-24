import { useCallback, useEffect, useMemo, useState } from 'react'

import { createApiClient, type CurrentEmployee, type Session } from './lib/api/client'
import { readApiBaseUrl } from './lib/auth/config'
import { useAuth } from './lib/auth/authContext'
import { ChatShell } from './features/messages/ChatShell'
import { NotificationCapability } from './features/notifications/NotificationCapability'
import { NotificationSettings } from './features/notifications/NotificationSettings'
import './App.css'

/**
 * The signed-in application: who you are, your devices (FR-005), and your conversations (US2).
 *
 * The profile and session surface stays because quickstart V1 walks through it, and because ending a
 * session is the only self-service control an employee has over their own access.
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

  const refreshMe = useCallback(async () => {
    setMe(await api.getMe())
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

      <NotificationCapability
        canReceiveNotifications={me.canReceiveNotifications}
        api={api}
        onEnabled={() => {
          void refreshMe()
        }}
      />

      <ChatShell
        authorized={api.authorized}
        getAccessToken={getAccessToken}
        currentEmployeeId={me.id}
      />

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

      <NotificationSettings api={api} />
    </main>
  )
}
