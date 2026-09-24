import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  detectClientNotificationCapability,
  enablePushNotifications,
} from '../src/lib/push/pushSubscription'
import { at } from './at'

/**
 * Push capability detection and enrolment (FR-034, FR-040).
 *
 * FR-040 needs two halves to produce a useful warning. The server knows whether it holds a live
 * subscription for this browser; only the browser knows *why* it does not — no push API here,
 * permission refused, or simply never enabled. Those three lead to three different things to tell
 * someone, and collapsing them into "notifications are off" leaves an employee with no idea whether
 * to change a browser setting, switch browsers, or click a button.
 *
 * jsdom supplies none of the push APIs, so each test installs exactly the globals the branch under
 * test needs. That is deliberate rather than a shortcut: stubbing them one at a time is what lets
 * the "unsupported" branches be exercised at all, and those are the branches that fire on the
 * browsers least likely to be tested by hand.
 */

const saved = {
  Notification: Reflect.get(globalThis, 'Notification') as unknown,
  PushManager: Reflect.get(globalThis, 'PushManager') as unknown,
  serviceWorker: Reflect.getOwnPropertyDescriptor(navigator, 'serviceWorker'),
}

/** Installs a Notification global whose permission and prompt result the test chooses. */
function stubNotification(permission: NotificationPermission, requested = permission) {
  const requestPermission = vi.fn(() => Promise.resolve(requested))

  Reflect.set(globalThis, 'Notification', { permission, requestPermission })

  return requestPermission
}

function stubPushManager() {
  Reflect.set(globalThis, 'PushManager', class {})
}

/** Installs a service-worker container whose subscription result the test chooses. */
function stubServiceWorker(subscription: unknown) {
  const subscribe = vi.fn(() => Promise.resolve(subscription))
  const register = vi.fn(() => Promise.resolve({ pushManager: { subscribe } }))

  Object.defineProperty(navigator, 'serviceWorker', {
    configurable: true,
    value: { register, ready: Promise.resolve({}) },
  })

  return { register, subscribe }
}

beforeEach(() => {
  vi.restoreAllMocks()
})

afterEach(() => {
  Reflect.set(globalThis, 'Notification', saved.Notification)
  Reflect.set(globalThis, 'PushManager', saved.PushManager)

  if (saved.serviceWorker) {
    Object.defineProperty(navigator, 'serviceWorker', saved.serviceWorker)
  } else {
    Reflect.deleteProperty(navigator, 'serviceWorker')
  }
})

describe('capability detection', () => {
  it('reports unsupported when the browser has no service worker', () => {
    Reflect.deleteProperty(navigator, 'serviceWorker')
    stubPushManager()
    stubNotification('default')

    expect(detectClientNotificationCapability()).toBe('unsupported')
  })

  it('reports unsupported when the browser has no PushManager', () => {
    stubServiceWorker({})
    Reflect.deleteProperty(globalThis, 'PushManager')
    stubNotification('default')

    // Safari before 16.4, and every iOS browser before then. Telling those employees to "allow
    // notifications" sends them looking for a setting that does not exist.
    expect(detectClientNotificationCapability()).toBe('unsupported')
  })

  it('reports unsupported when there is no Notification API', () => {
    stubServiceWorker({})
    stubPushManager()
    Reflect.deleteProperty(globalThis, 'Notification')

    expect(detectClientNotificationCapability()).toBe('unsupported')
  })

  it('reports a refusal separately from never having been enabled', () => {
    stubServiceWorker({})
    stubPushManager()

    stubNotification('denied')
    expect(detectClientNotificationCapability()).toBe('permission_denied')

    // The distinction that matters: "denied" means a browser setting has to be changed, and no
    // button in this app can do it. "not_enabled" means one click will.
    stubNotification('default')
    expect(detectClientNotificationCapability()).toBe('not_enabled')

    stubNotification('granted')
    expect(detectClientNotificationCapability()).toBe('not_enabled')
  })
})

describe('enrolment', () => {
  const usable = {
    toJSON: () => ({
      endpoint: 'https://push.example.test/abc',
      keys: { p256dh: 'public-key', auth: 'auth-secret' },
    }),
  }

  it('asks for permission, registers the worker, then subscribes', async () => {
    const requestPermission = stubNotification('default', 'granted')
    const { register, subscribe } = stubServiceWorker(usable)

    const details = await enablePushNotifications('BFakeKey-_')

    // Order matters: each step only makes sense once the one before it succeeded. Registering a
    // worker for someone who then refuses permission leaves a worker installed for nothing.
    expect(requestPermission).toHaveBeenCalled()

    // A fixed root-relative path. Vite's `new URL(..., import.meta.url)` handling applies to the
    // Worker constructors, not to ServiceWorkerContainer.register, so that form would resolve at
    // runtime without ever having been built.
    expect(register).toHaveBeenCalledWith('/service-worker.js', { type: 'module' })

    expect(subscribe).toHaveBeenCalledWith(
      expect.objectContaining({ userVisibleOnly: true }) as unknown,
    )

    expect(details).toEqual({
      endpoint: 'https://push.example.test/abc',
      p256dh: 'public-key',
      auth: 'auth-secret',
    })
  })

  it('decodes the VAPID key from url-safe base64 into raw bytes', async () => {
    stubNotification('default', 'granted')
    const { subscribe } = stubServiceWorker(usable)

    // '-' and '_' are the url-safe substitutions, and the string is unpadded — both of which atob
    // rejects outright. A key decoded wrongly produces a subscription the server cannot encrypt to,
    // and the failure surfaces much later as notifications that never arrive.
    await enablePushNotifications('q-_A')

    const key = (at(at(subscribe.mock.calls, 0), 0) as { applicationServerKey: Uint8Array })
      .applicationServerKey

    expect(key).toBeInstanceOf(Uint8Array)
    expect([...key]).toEqual([0xab, 0xef, 0xc0])

    // A plain ArrayBuffer, not ArrayBufferLike: PushSubscriptionOptionsInit takes a BufferSource,
    // and a SharedArrayBuffer is a valid ArrayBufferLike that it rejects.
    expect(key.buffer).toBeInstanceOf(ArrayBuffer)
  })

  it('does not register anything when permission is refused', async () => {
    stubNotification('default', 'denied')
    const { register } = stubServiceWorker(usable)

    await expect(enablePushNotifications('BKey')).rejects.toThrow(/permission/i)

    expect(register).not.toHaveBeenCalled()
  })

  it('refuses a subscription the browser returned incomplete', async () => {
    stubNotification('default', 'granted')

    stubServiceWorker({
      // Endpoint but no keys. Sending this to the server would store a subscription that can never
      // be encrypted to — every notification would fail, silently, forever.
      toJSON: () => ({ endpoint: 'https://push.example.test/abc', keys: undefined }),
    })

    await expect(enablePushNotifications('BKey')).rejects.toThrow(/usable push subscription/i)
  })

  it('refuses a subscription with no endpoint', async () => {
    stubNotification('default', 'granted')

    stubServiceWorker({
      toJSON: () => ({ endpoint: undefined, keys: { p256dh: 'k', auth: 'a' } }),
    })

    await expect(enablePushNotifications('BKey')).rejects.toThrow(/usable push subscription/i)
  })

  it('refuses a subscription missing only the auth secret', async () => {
    stubNotification('default', 'granted')

    stubServiceWorker({
      toJSON: () => ({ endpoint: 'https://push.example.test/abc', keys: { p256dh: 'k' } }),
    })

    await expect(enablePushNotifications('BKey')).rejects.toThrow(/usable push subscription/i)
  })
})
