/**
 * T138 — the service worker. Handles push and nothing else (research.md D13).
 *
 * Deliberately minimal: no offline cache, no asset interception, no update logic beyond the
 * browser's own. Every one of those is a way a service worker can start serving stale content or
 * breaking navigation, and none of them is what this platform needs — FR-034 only requires an OS
 * notification to appear while the tab is not in front of the reader.
 *
 * Typed against a small local interface rather than TypeScript's `WebWorker` lib: the main app's
 * `tsconfig.app.json` compiles this file too (under `include: ["src"]`) with `lib: ["DOM"]`, and
 * `WebWorker` conflicts with `DOM` in the same program. A dedicated service-worker tsconfig would
 * fix that at the cost of a second project reference; casting `self` here is the smaller change
 * for a file this short.
 */

interface PushMessagePayload {
  readonly title: string
  readonly body: string
  readonly deepLink: string
}

interface NotificationOptionsLike {
  readonly body?: string
  readonly data?: unknown
}

interface NotificationLike {
  readonly data: unknown
  close(): void
}

interface PushEventLike {
  readonly data: { json(): unknown } | null
  waitUntil(promise: Promise<unknown>): void
}

interface NotificationClickEventLike {
  readonly notification: NotificationLike
  waitUntil(promise: Promise<unknown>): void
}

interface ClientsLike {
  openWindow(url: string): Promise<unknown>
}

interface ServiceWorkerScopeLike {
  registration: {
    showNotification(title: string, options: NotificationOptionsLike): Promise<void>
  }
  clients: ClientsLike
  addEventListener(type: 'push', listener: (event: PushEventLike) => void): void
  addEventListener(
    type: 'notificationclick',
    listener: (event: NotificationClickEventLike) => void,
  ): void
}

const scope = self as unknown as ServiceWorkerScopeLike

/** Falls back to a generic notification for a payload this version does not recognise. */
function readPayload(event: PushEventLike): PushMessagePayload {
  const raw: unknown = event.data?.json()

  if (
    typeof raw === 'object' &&
    raw !== null &&
    'title' in raw &&
    'body' in raw &&
    typeof (raw as { title: unknown }).title === 'string' &&
    typeof (raw as { body: unknown }).body === 'string'
  ) {
    const candidate = raw as { title: string; body: string; deepLink?: unknown }

    return {
      title: candidate.title,
      body: candidate.body,
      deepLink: typeof candidate.deepLink === 'string' ? candidate.deepLink : '/',
    }
  }

  return { title: 'New message', body: 'Open InternalChat to see what changed.', deepLink: '/' }
}

scope.addEventListener('push', (event) => {
  const payload = readPayload(event)

  event.waitUntil(
    scope.registration.showNotification(payload.title, {
      body: payload.body,
      data: { deepLink: payload.deepLink },
    }),
  )
})

// FR-039: opening a notification takes the employee to the triggering message.
scope.addEventListener('notificationclick', (event) => {
  event.notification.close()

  const data = event.notification.data
  const deepLink =
    typeof data === 'object' && data !== null && 'deepLink' in data ? String(data.deepLink) : '/'

  event.waitUntil(scope.clients.openWindow(deepLink))
})
