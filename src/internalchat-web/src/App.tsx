import { useCallback, useEffect, useMemo, useState } from 'react'

import { LogOut } from 'lucide-react'

import { createApiClient, type CurrentEmployee, type Session } from './lib/api/client'
import { readApiBaseUrl } from './lib/auth/config'
import { useAuth } from './lib/auth/authContext'
import { ChatShell } from './features/messages/ChatShell'
import { NotificationCapability } from './features/notifications/NotificationCapability'
import { NotificationSettings } from './features/notifications/NotificationSettings'
import { RetentionNotice } from './features/settings/RetentionNotice'
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
    <div className="app-container">
      <aside className="app-sidebar">
        <header className="app-header">
          <div className="profile-info">
            <div className="avatar">
              {me.displayName.charAt(0)}
            </div>
            <div>
              <h1 data-testid="display-name" className="profile-name">{me.displayName}</h1>
              <p className="profile-email">{me.email}</p>
            </div>
          </div>
          <button type="button" className="btn-secondary sign-out-btn" onClick={signOut}>
            <LogOut size={16} /> Sign out
          </button>
        </header>

        <div className="sidebar-sections">
          <NotificationCapability
            canReceiveNotifications={me.canReceiveNotifications}
            api={api}
            onEnabled={() => {
              void refreshMe()
            }}
          />

          <section aria-labelledby="sessions-heading" className="settings-section">
            <h2 id="sessions-heading">Your Devices</h2>
            <ul className="session-list">
              {sessions.map((session) => (
                <li key={session.id} data-testid="session" className="session-item">
                  <div className="session-info">
                    <span className="device-name">{session.userAgent || 'Unknown device'}</span>
                    {session.isCurrent && <span className="current-badge">This device</span>}
                  </div>
                  <button
                    type="button"
                    className="btn-danger-text"
                    onClick={() => {
                      void revoke(session.id)
                    }}
                  >
                    Sign out
                  </button>
                </li>
              ))}
            </ul>
          </section>

          <div className="settings-section">
            <NotificationSettings api={api} />
          </div>
          <div className="settings-section">
            <RetentionNotice api={api} />
          </div>
        </div>
      </aside>

      <main className="app-main">
        <ChatShell
          authorized={api.authorized}
          getAccessToken={getAccessToken}
          currentEmployeeId={me.id}
        />
      </main>
    </div>
  )
}
