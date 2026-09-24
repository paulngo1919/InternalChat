/**
 * T140 — do-not-disturb settings (FR-037, FR-038).
 *
 * `<input type="time">` is not decoration: the browser's own picker produces exactly the `"HH:mm"`
 * string the API's `NotificationPreferences.dndStart`/`dndEnd` expect, so there is no format to get
 * wrong between the two.
 */

import { useEffect, useState, type SyntheticEvent } from 'react'

import type { ApiClient, NotificationPreferences } from '../../lib/api/client'

interface NotificationSettingsProps {
  readonly api: ApiClient
}

/** A do-not-disturb window and digest threshold, editable and saved on submit. */
export function NotificationSettings({ api }: NotificationSettingsProps) {
  const [preferences, setPreferences] = useState<NotificationPreferences | null>(null)
  const [draft, setDraft] = useState<NotificationPreferences | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    async function load(): Promise<void> {
      try {
        const loaded = await api.getNotificationPreferences()

        if (!cancelled) {
          setPreferences(loaded)
          setDraft(loaded)
        }
      } catch (cause) {
        if (!cancelled) {
          setError(cause instanceof Error ? cause.message : 'Could not load notification settings.')
        }
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [api])

  if (!draft) {
    return error ? <p role="alert">{error}</p> : <p>Loading notification settings…</p>
  }

  const dndEnabled = draft.dndStart !== null && draft.dndEnd !== null

  const save = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault()
    setSaving(true)
    setError(null)

    void (async () => {
      try {
        const saved = await api.updateNotificationPreferences(draft)
        setPreferences(saved)
        setDraft(saved)
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'Could not save notification settings.')
      } finally {
        setSaving(false)
      }
    })()
  }

  const unsaved = preferences !== null && JSON.stringify(preferences) !== JSON.stringify(draft)

  return (
    <section aria-labelledby="notification-settings-heading">
      <h2 id="notification-settings-heading">Notification settings</h2>

      <form onSubmit={save}>
        <label>
          <input
            type="checkbox"
            checked={dndEnabled}
            onChange={(event) => {
              setDraft((current) =>
                current === null
                  ? current
                  : {
                      ...current,
                      dndStart: event.target.checked ? '18:00' : null,
                      dndEnd: event.target.checked ? '08:00' : null,
                    },
              )
            }}
          />
          Do not disturb
        </label>

        {dndEnabled && (
          <p>
            <label htmlFor="dnd-start">From</label>
            <input
              id="dnd-start"
              type="time"
              value={draft.dndStart}
              onChange={(event) => {
                setDraft((current) =>
                  current === null ? current : { ...current, dndStart: event.target.value },
                )
              }}
            />

            <label htmlFor="dnd-end">To</label>
            <input
              id="dnd-end"
              type="time"
              value={draft.dndEnd}
              onChange={(event) => {
                setDraft((current) =>
                  current === null ? current : { ...current, dndEnd: event.target.value },
                )
              }}
            />
          </p>
        )}

        <label htmlFor="digest-after-minutes">
          Batch a backlog into one summary after being unreachable for (minutes)
        </label>
        <input
          id="digest-after-minutes"
          type="number"
          min={1}
          max={1440}
          value={draft.digestAfterMinutes}
          onChange={(event) => {
            const minutes = Number(event.target.value)
            setDraft((current) =>
              current === null ? current : { ...current, digestAfterMinutes: minutes },
            )
          }}
        />

        {error && <p role="alert">{error}</p>}

        <button type="submit" disabled={saving || !unsaved}>
          {saving ? 'Saving…' : 'Save'}
        </button>
      </form>
    </section>
  )
}
