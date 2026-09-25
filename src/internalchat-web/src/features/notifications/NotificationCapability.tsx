/**
 * T139 — notification capability detection and the plain-language warning FR-040 requires.
 *
 * FR-040: "System MUST detect when an employee's current device or browser cannot receive
 * notifications, tell them so plainly, and give them the steps to enable it." Two independent
 * facts feed the message: the server's `canReceiveNotifications` (does it hold a live subscription
 * for this employee at all) and the browser's own capability (API support, permission, whether
 * this device has ever enabled it) — only the browser can see the second half.
 */

import { useState } from 'react'
import { BellRing } from 'lucide-react'

import type { ApiClient } from '../../lib/api/client'
import {
  detectClientNotificationCapability,
  enablePushNotifications,
} from '../../lib/push/pushSubscription'

interface NotificationCapabilityProps {
  readonly canReceiveNotifications: boolean
  readonly api: ApiClient
  /** Called after a subscription is successfully registered, so the caller can refresh `/me`. */
  readonly onEnabled: () => void
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
}

/** Warns plainly when this device cannot be reached, and offers to fix it when that is possible. */
export function NotificationCapability({
  canReceiveNotifications,
  api,
  onEnabled,
}: NotificationCapabilityProps) {
  const [enabling, setEnabling] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // The server already knows this browser is covered — nothing to warn about, and re-detecting
  // client capability would be a second, redundant source of truth for the same fact.
  if (canReceiveNotifications) {
    return null
  }

  const capability = detectClientNotificationCapability()
  const canOffer = capability === 'not_enabled'

  const enable = () => {
    setEnabling(true)
    setError(null)

    void (async () => {
      try {
        const { publicKey } = await api.getVapidPublicKey()
        const subscription = await enablePushNotifications(publicKey)
        await api.registerPushSubscription(subscription)
        onEnabled()
      } catch (cause) {
        setError(cause instanceof Error ? cause.message : 'Could not enable notifications.')
      } finally {
        setEnabling(false)
      }
    })()
  }

  return (
    <div role="status" data-testid="notification-capability-warning">
      <p>{MESSAGES[capability]}</p>

      {canOffer && (
        <button type="button" className="btn-primary btn-sm mt-8" onClick={enable} disabled={enabling} style={{ marginTop: '8px' }}>
          {enabling ? <><BellRing size={14} /> Enabling…</> : <><BellRing size={14} /> Enable notifications</>}
        </button>
      )}

      {error && <p role="alert">{error}</p>}
    </div>
  )
}
