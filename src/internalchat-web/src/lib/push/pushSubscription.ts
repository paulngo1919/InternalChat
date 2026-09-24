/**
 * T139 — detecting and enabling push notifications (FR-034, FR-040).
 *
 * Split from the component that renders it, for the same Fast Refresh reason
 * `conversations/queryKeys.ts` is split from its components.
 */

/**
 * What this browser can tell us, independent of whether the server holds a subscription for it.
 * FR-040 needs both halves: the server can say "no live subscription", but only the browser can
 * say *why* — no API exists here, permission was refused, or nothing has enabled it yet.
 */
export type ClientNotificationCapability = 'unsupported' | 'permission_denied' | 'not_enabled'

/** Reads the three facts that decide FR-040's warning, without touching anything server-side. */
export function detectClientNotificationCapability(): ClientNotificationCapability {
  if (
    typeof navigator === 'undefined' ||
    !('serviceWorker' in navigator) ||
    typeof window === 'undefined' ||
    !('PushManager' in window) ||
    typeof Notification === 'undefined'
  ) {
    return 'unsupported'
  }

  if (Notification.permission === 'denied') {
    return 'permission_denied'
  }

  return 'not_enabled'
}

/** Decodes the VAPID public key into the raw bytes `PushManager.subscribe` requires. */
function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4)
  const normalized = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/')
  const binary = atob(normalized)

  // Not `Uint8Array.from`: it infers `Uint8Array<ArrayBufferLike>`, which
  // `PushSubscriptionOptionsInit.applicationServerKey` (typed as `BufferSource`) rejects — a
  // `SharedArrayBuffer` is a valid `ArrayBufferLike` but not a valid `ArrayBuffer`. Allocating the
  // concrete length up front keeps the backing buffer a plain `ArrayBuffer`.
  const bytes = new Uint8Array(binary.length)

  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }

  return bytes
}

/** What the server needs to deliver push to this browser. */
export interface PushSubscriptionDetails {
  readonly endpoint: string
  readonly p256dh: string
  readonly auth: string
}

/**
 * Requests permission, registers the service worker, and subscribes — in that order, since each
 * step only makes sense once the one before it succeeded.
 */
export async function enablePushNotifications(
  vapidPublicKey: string,
): Promise<PushSubscriptionDetails> {
  const permission = await Notification.requestPermission()

  if (permission !== 'granted') {
    throw new Error('Notification permission was not granted.')
  }

  // A fixed, root-relative path rather than `new URL(..., import.meta.url)`: Vite's automatic
  // asset-URL handling for that pattern applies to the `Worker`/`SharedWorker` constructors, not
  // to `ServiceWorkerContainer.register`, so it would resolve at runtime without ever being built.
  // `vite.config.ts` emits `service-worker.ts` at this exact path via a second Rollup input.
  const registration = await navigator.serviceWorker.register('/service-worker.js', {
    type: 'module',
  })
  await navigator.serviceWorker.ready

  const subscription = await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: urlBase64ToUint8Array(vapidPublicKey),
  })

  const json = subscription.toJSON()

  if (!json.endpoint || !json.keys?.p256dh || !json.keys.auth) {
    throw new Error('The browser did not return a usable push subscription.')
  }

  return { endpoint: json.endpoint, p256dh: json.keys.p256dh, auth: json.keys.auth }
}
