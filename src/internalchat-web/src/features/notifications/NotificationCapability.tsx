/**
 * T139 — notification capability detection and plain-language warning (FR-040).
 */

import { useState } from 'react';
import { BellRing, ShieldAlert } from 'lucide-react';

import type { ApiClient } from '../../lib/api/client';
import {
  detectClientNotificationCapability,
  enablePushNotifications,
} from '../../lib/push/pushSubscription';

interface NotificationCapabilityProps {
  readonly canReceiveNotifications: boolean;
  readonly api: ApiClient;
  readonly onEnabled: () => void;
}

const MESSAGES: Record<ReturnType<typeof detectClientNotificationCapability>, string> = {
  unsupported:
    'This browser cannot receive notifications. You will not be alerted to new messages while ' +
    'this tab is in the background — check for unread conversations directly instead.',
  permission_denied:
    "Notifications are blocked for this site. Allow them in your browser's site settings, then " +
    'reload this page.',
  not_enabled:
    'Notifications are off for this browser. Turn them on to be alerted to new messages.',
};

export function NotificationCapability({
  canReceiveNotifications,
  api,
  onEnabled,
}: NotificationCapabilityProps) {
  const [enabling, setEnabling] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (canReceiveNotifications) {
    return null;
  }

  const capability = detectClientNotificationCapability();
  const canOffer = capability === 'not_enabled';

  const enable = () => {
    setEnabling(true);
    setError(null);

    void (async () => {
      try {
        const { publicKey } = await api.getVapidPublicKey();
        const subscription = await enablePushNotifications(publicKey);
        await api.registerPushSubscription(subscription);
        onEnabled();
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'Could not enable notifications.');
      } finally {
        setEnabling(false);
      }
    })();
  };

  return (
    <div
      role="status"
      data-testid="notification-capability-warning"
      className="notification-banner-card"
    >
      <div className="notification-banner-header">
        <ShieldAlert size={16} className="text-warning" />
        <span className="notification-banner-title">Push Notifications</span>
      </div>
      <p className="notification-banner-text">{MESSAGES[capability]}</p>

      {canOffer && (
        <button
          type="button"
          className="btn-primary btn-sm"
          onClick={enable}
          disabled={enabling}
          style={{ marginTop: '8px', width: '100%' }}
        >
          {enabling ? (
            <>
              <BellRing size={14} /> Enabling…
            </>
          ) : (
            <>
              <BellRing size={14} /> Enable notifications
            </>
          )}
        </button>
      )}

      {error && (
        <p role="alert" className="error-text" style={{ marginTop: '6px' }}>
          {error}
        </p>
      )}
    </div>
  );
}
