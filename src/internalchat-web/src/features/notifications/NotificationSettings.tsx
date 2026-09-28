/**
 * T140 — do-not-disturb settings (FR-037, FR-038).
 */

import { useEffect, useState, type SyntheticEvent } from 'react';
import { Save, Sliders } from 'lucide-react';

import { Spinner } from '../../components/loading/Skeletons';
import type { ApiClient, NotificationPreferences } from '../../lib/api/client';

interface NotificationSettingsProps {
  readonly api: ApiClient;
}

export function NotificationSettings({ api }: NotificationSettingsProps) {
  const [preferences, setPreferences] = useState<NotificationPreferences | null>(null);
  const [draft, setDraft] = useState<NotificationPreferences | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;

    async function load(): Promise<void> {
      try {
        const loaded = await api.getNotificationPreferences();

        if (!cancelled) {
          setPreferences(loaded);
          setDraft(loaded);
        }
      } catch (cause) {
        if (!cancelled) {
          setError(
            cause instanceof Error ? cause.message : 'Could not load notification settings.',
          );
        }
      }
    }

    void load();

    return () => {
      cancelled = true;
    };
  }, [api]);

  if (!draft) {
    return error ? (
      <p role="alert" className="error-text">
        {error}
      </p>
    ) : (
      <Spinner label="Loading notification settings…" />
    );
  }

  const dndEnabled = draft.dndStart !== null && draft.dndEnd !== null;

  const save = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    setSaving(true);
    setError(null);

    void (async () => {
      try {
        const saved = await api.updateNotificationPreferences(draft);
        setPreferences(saved);
        setDraft(saved);
      } catch (cause) {
        setError(
          cause instanceof Error ? cause.message : 'Could not save notification settings.',
        );
      } finally {
        setSaving(false);
      }
    })();
  };

  const unsaved = preferences !== null && JSON.stringify(preferences) !== JSON.stringify(draft);

  return (
    <section aria-labelledby="notification-settings-heading">
      <h2 id="notification-settings-heading" className="sidebar-section-title">
        <Sliders size={14} style={{ marginRight: '6px' }} />
        Notification settings
      </h2>

      <form onSubmit={save} className="settings-form">
        <label className="checkbox-label">
          <input
            type="checkbox"
            className="checkbox-input"
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
              );
            }}
          />
          <span>Do not disturb</span>
        </label>

        {dndEnabled && (
          <div className="time-range-group">
            <div className="time-field">
              <label htmlFor="dnd-start">From</label>
              <input
                id="dnd-start"
                type="time"
                className="time-input"
                value={draft.dndStart}
                onChange={(event) => {
                  setDraft((current) =>
                    current === null ? current : { ...current, dndStart: event.target.value },
                  );
                }}
              />
            </div>

            <div className="time-field">
              <label htmlFor="dnd-end">To</label>
              <input
                id="dnd-end"
                type="time"
                className="time-input"
                value={draft.dndEnd}
                onChange={(event) => {
                  setDraft((current) =>
                    current === null ? current : { ...current, dndEnd: event.target.value },
                  );
                }}
              />
            </div>
          </div>
        )}

        <div className="form-group-field">
          <label htmlFor="digest-after-minutes" className="field-label">
            Batch a backlog into one summary after being unreachable for (minutes)
          </label>
          <input
            id="digest-after-minutes"
            type="number"
            min={1}
            max={1440}
            className="number-input"
            value={draft.digestAfterMinutes}
            onChange={(event) => {
              const minutes = Number(event.target.value);
              setDraft((current) =>
                current === null ? current : { ...current, digestAfterMinutes: minutes },
              );
            }}
          />
        </div>

        {error && (
          <p role="alert" className="error-text">
            {error}
          </p>
        )}

        <button
          type="submit"
          className="btn-primary btn-sm"
          disabled={saving || !unsaved}
          style={{ marginTop: '8px', width: '100%' }}
        >
          {saving ? (
            <>
              <Save size={14} /> Saving…
            </>
          ) : (
            <>
              <Save size={14} /> Save
            </>
          )}
        </button>
      </form>
    </section>
  );
}
