import { beforeEach, describe, expect, it, vi } from 'vitest'

/**
 * The service worker: push display and deep-link navigation (FR-034, FR-039).
 *
 * <b>This is the one piece of this app that runs when nobody is looking at it.</b> A component that
 * throws produces a blank panel someone reports; a service worker that throws produces a
 * notification that never appears, for one employee, on one device, with no error anywhere they
 * would see it. The failure mode is silence, which is why the payload handling is defensive and why
 * the defensive branches are what these tests exercise.
 *
 * The worker registers its listeners at module load against `self`, so the globals have to be in
 * place before the import. Hence the dynamic import after stubbing, and `resetModules` between
 * tests so each one gets its own registration.
 */

type Listener = (event: unknown) => void

/** A stand-in service-worker global that records what the worker did. */
function installScope() {
  const listeners = new Map<string, Listener>()
  const showNotification = vi.fn(() => Promise.resolve())
  const openWindow = vi.fn(() => Promise.resolve())

  Reflect.set(globalThis, 'registration', { showNotification })

  Object.assign(globalThis, {
    registration: { showNotification },
    clients: { openWindow },
    addEventListener: (type: string, listener: Listener) => listeners.set(type, listener),
  })

  return { listeners, showNotification, openWindow }
}

/** A push event whose payload the test chooses. `waitUntil` is awaited so assertions can follow. */
function pushEvent(json: unknown) {
  const held: Promise<unknown>[] = []

  return {
    event: { data: { json: () => json }, waitUntil: (p: Promise<unknown>) => held.push(p) },
    settled: () => Promise.all(held),
  }
}

beforeEach(() => {
  vi.resetModules()
  vi.restoreAllMocks()
})

describe('push', () => {
  it('shows the notification the server sent', async () => {
    const scope = installScope()
    await import('../src/lib/push/service-worker')

    const { event, settled } = pushEvent({
      title: 'An Nguyen',
      body: 'Can you look at the deploy?',
      deepLink: '/conversations/c1?seq=42',
    })

    scope.listeners.get('push')?.(event)
    await settled()

    expect(scope.showNotification).toHaveBeenCalledWith('An Nguyen', {
      body: 'Can you look at the deploy?',
      data: { deepLink: '/conversations/c1?seq=42' },
    })
  })

  it('falls back to a generic notification for a payload it does not recognise', async () => {
    const scope = installScope()
    await import('../src/lib/push/service-worker')

    // A payload shape from a future server version, or a malformed one. An installed worker can be
    // months older than the server that pushes to it, and throwing here would mean no notification
    // at all rather than a vague one.
    const { event, settled } = pushEvent({ kind: 'something.new.v2' })

    scope.listeners.get('push')?.(event)
    await settled()

    expect(scope.showNotification).toHaveBeenCalledWith('New message', {
      body: 'Open InternalChat to see what changed.',
      data: { deepLink: '/' },
    })
  })

  it('falls back when title or body is not a string', async () => {
    const scope = installScope()
    await import('../src/lib/push/service-worker')

    const { event, settled } = pushEvent({ title: 42, body: 'text' })

    scope.listeners.get('push')?.(event)
    await settled()

    expect(scope.showNotification).toHaveBeenCalledWith(
      'New message',
      expect.objectContaining({ data: { deepLink: '/' } }) as unknown,
    )
  })

  it('falls back when the push carried no data at all', async () => {
    const scope = installScope()
    await import('../src/lib/push/service-worker')

    const held: Promise<unknown>[] = []

    // A push with no payload is legal, and some push services send one to wake a worker.
    scope.listeners.get('push')?.({
      data: null,
      waitUntil: (p: Promise<unknown>) => held.push(p),
    })

    await Promise.all(held)

    expect(scope.showNotification).toHaveBeenCalledWith('New message', expect.anything())
  })

  it('defaults the deep link when the payload omits it', async () => {
    const scope = installScope()
    await import('../src/lib/push/service-worker')

    const { event, settled } = pushEvent({ title: 'An', body: 'hi', deepLink: 99 })

    scope.listeners.get('push')?.(event)
    await settled()

    // Root, not the number. Opening `/99` would land on a route that does not exist, from a
    // notification the employee tapped expecting to see a message.
    expect(scope.showNotification).toHaveBeenCalledWith(
      'An',
      expect.objectContaining({ data: { deepLink: '/' } }) as unknown,
    )
  })
})

describe('notificationclick', () => {
  it('closes the notification and opens the triggering message', async () => {
    const scope = installScope()
    await import('../src/lib/push/service-worker')

    const close = vi.fn()
    const held: Promise<unknown>[] = []

    scope.listeners.get('notificationclick')?.({
      notification: { data: { deepLink: '/conversations/c1?seq=42' }, close },
      waitUntil: (p: Promise<unknown>) => held.push(p),
    })

    await Promise.all(held)

    // FR-039: opening a notification takes the employee to the triggering message, not to the app's
    // front door for them to find it again themselves.
    expect(close).toHaveBeenCalled()
    expect(scope.openWindow).toHaveBeenCalledWith('/conversations/c1?seq=42')
  })

  it('opens the root when the notification carries no deep link', async () => {
    const scope = installScope()
    await import('../src/lib/push/service-worker')

    const held: Promise<unknown>[] = []

    scope.listeners.get('notificationclick')?.({
      notification: { data: null, close: vi.fn() },
      waitUntil: (p: Promise<unknown>) => held.push(p),
    })

    await Promise.all(held)

    expect(scope.openWindow).toHaveBeenCalledWith('/')
  })

  it('opens the root when the data has no deepLink field', async () => {
    const scope = installScope()
    await import('../src/lib/push/service-worker')

    const held: Promise<unknown>[] = []

    scope.listeners.get('notificationclick')?.({
      notification: { data: { other: 'x' }, close: vi.fn() },
      waitUntil: (p: Promise<unknown>) => held.push(p),
    })

    await Promise.all(held)

    expect(scope.openWindow).toHaveBeenCalledWith('/')
  })
})
