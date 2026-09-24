import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import type { NotificationPreferences } from '../src/lib/api/client'
import { NotificationCapability } from '../src/features/notifications/NotificationCapability'
import { NotificationSettings } from '../src/features/notifications/NotificationSettings'
import { fakeApiClient } from './helpers'
import { pending } from './at'

/**
 * Notification settings and the FR-040 capability warning.
 *
 * <b>FR-040 is the reason the warning component exists, and it is unusual in being a requirement
 * about admitting a limitation.</b> An employee whose browser cannot receive notifications is not
 * failing at anything — but if nobody tells them, they conclude the platform is quiet when it is
 * not. Two independent facts feed the message: the server knows whether it holds a live
 * subscription, and only the browser knows why it does not. The three reasons lead to three
 * different things to do, and collapsing them is what makes a warning useless.
 */

const saved = {
  Notification: Reflect.get(globalThis, 'Notification') as unknown,
  PushManager: Reflect.get(globalThis, 'PushManager') as unknown,
  serviceWorker: Reflect.getOwnPropertyDescriptor(navigator, 'serviceWorker'),
}

/** Makes the browser look capable, with the given permission state. */
function browserWith(permission: NotificationPermission) {
  Reflect.set(globalThis, 'PushManager', class {})
  Reflect.set(globalThis, 'Notification', {
    permission,
    requestPermission: () => Promise.resolve('granted'),
  })

  Object.defineProperty(navigator, 'serviceWorker', {
    configurable: true,
    value: {
      register: () =>
        Promise.resolve({
          pushManager: {
            subscribe: () =>
              Promise.resolve({
                toJSON: () => ({
                  endpoint: 'https://push.example.test/abc',
                  keys: { p256dh: 'key', auth: 'secret' },
                }),
              }),
          },
        }),
      ready: Promise.resolve({}),
    },
  })
}

/** Removes every push API, as an older browser would. */
function browserWithout() {
  Reflect.deleteProperty(globalThis, 'PushManager')
  Reflect.deleteProperty(globalThis, 'Notification')
  Reflect.deleteProperty(navigator, 'serviceWorker')
}

beforeEach(() => {
  vi.restoreAllMocks()
})

afterEach(() => {
  Reflect.set(globalThis, 'Notification', saved.Notification)
  Reflect.set(globalThis, 'PushManager', saved.PushManager)

  if (saved.serviceWorker) {
    Object.defineProperty(navigator, 'serviceWorker', saved.serviceWorker)
  }

  cleanup()
})

describe('NotificationCapability', () => {
  it('says nothing when the server already holds a live subscription', () => {
    browserWith('granted')

    render(
      <NotificationCapability
        canReceiveNotifications
        api={fakeApiClient()}
        onEnabled={() => undefined}
      />,
    )

    // Nothing to warn about, and re-detecting client capability would be a second, redundant source
    // of truth for the same fact.
    expect(screen.queryByTestId('notification-capability-warning')).not.toBeInTheDocument()
  })

  it('tells someone on an unsupported browser what to do instead', () => {
    browserWithout()

    render(
      <NotificationCapability
        canReceiveNotifications={false}
        api={fakeApiClient()}
        onEnabled={() => undefined}
      />,
    )

    const warning = screen.getByTestId('notification-capability-warning')

    expect(warning).toHaveTextContent('cannot receive notifications')

    // The actionable half. Telling them to "allow notifications" would send them looking for a
    // setting their browser does not have.
    expect(warning).toHaveTextContent('check for unread conversations directly')
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('sends someone who blocked notifications to their browser settings', () => {
    browserWith('denied')

    render(
      <NotificationCapability
        canReceiveNotifications={false}
        api={fakeApiClient()}
        onEnabled={() => undefined}
      />,
    )

    expect(screen.getByTestId('notification-capability-warning')).toHaveTextContent(
      /site settings/i,
    )

    // No button: nothing this page can do will un-deny a permission, and offering one that silently
    // fails is worse than offering none.
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('offers to turn them on when that is all it takes', () => {
    browserWith('default')

    render(
      <NotificationCapability
        canReceiveNotifications={false}
        api={fakeApiClient()}
        onEnabled={() => undefined}
      />,
    )

    expect(screen.getByRole('button', { name: 'Enable notifications' })).toBeEnabled()
  })

  it('fetches the key, subscribes, registers, and tells the caller', async () => {
    browserWith('default')

    const api = fakeApiClient()
    const onEnabled = vi.fn()

    render(
      <NotificationCapability canReceiveNotifications={false} api={api} onEnabled={onEnabled} />,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Enable notifications' }))

    await waitFor(() => {
      expect(api.registerPushSubscription).toHaveBeenCalledWith({
        endpoint: 'https://push.example.test/abc',
        p256dh: 'key',
        auth: 'secret',
      })
    })

    // So the caller can refetch /me and stop showing the warning.
    expect(onEnabled).toHaveBeenCalled()
  })

  it('reports a failure and re-enables the button', async () => {
    browserWith('default')

    const api = fakeApiClient({
      getVapidPublicKey: vi.fn(() => Promise.reject(new Error('The API returned 503.'))),
    })

    render(
      <NotificationCapability
        canReceiveNotifications={false}
        api={api}
        onEnabled={() => undefined}
      />,
    )

    fireEvent.click(screen.getByRole('button', { name: 'Enable notifications' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The API returned 503.')

    // Re-enabled, because this is exactly the kind of failure that succeeds on a second try.
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Enable notifications' })).toBeEnabled()
    })
  })
})

describe('NotificationSettings', () => {
  it('reports loading and failure distinctly', async () => {
    render(
      <NotificationSettings
        api={fakeApiClient({
          getNotificationPreferences: vi.fn(() => pending<NotificationPreferences>()),
        })}
      />,
    )

    expect(screen.getByText(/loading notification settings/i)).toBeInTheDocument()

    cleanup()

    render(
      <NotificationSettings
        api={fakeApiClient({
          getNotificationPreferences: vi.fn(() => Promise.reject(new Error('The API returned 503.'))),
        })}
      />,
    )

    expect(await screen.findByRole('alert')).toHaveTextContent('The API returned 503.')
  })

  it('shows no window controls when do-not-disturb is off', async () => {
    render(<NotificationSettings api={fakeApiClient()} />)

    await screen.findByLabelText('Do not disturb')

    expect(screen.getByLabelText('Do not disturb')).not.toBeChecked()
    expect(screen.queryByLabelText('From')).not.toBeInTheDocument()
  })

  it('offers a sensible default window when it is turned on', async () => {
    render(<NotificationSettings api={fakeApiClient()} />)

    fireEvent.click(await screen.findByLabelText('Do not disturb'))

    // Both ends at once. The server refuses a start without an end, so a control that could set one
    // alone would produce a 400 for an otherwise reasonable action.
    expect(screen.getByLabelText('From')).toHaveValue('18:00')
    expect(screen.getByLabelText('To')).toHaveValue('08:00')
  })

  it('clears both ends when it is turned back off', async () => {
    render(<NotificationSettings api={fakeApiClient()} />)

    const toggle = await screen.findByLabelText('Do not disturb')

    fireEvent.click(toggle)
    fireEvent.click(toggle)

    expect(screen.queryByLabelText('From')).not.toBeInTheDocument()
  })

  it('saves nothing until something has changed', async () => {
    const api = fakeApiClient()

    render(<NotificationSettings api={api} />)

    const save = await screen.findByRole('button', { name: 'Save' })

    // A save that writes the values it just read is a request that can only fail or do nothing.
    expect(save).toBeDisabled()

    fireEvent.click(await screen.findByLabelText('Do not disturb'))

    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled()
  })

  it('saves the whole preference set and becomes clean again', async () => {
    const api = fakeApiClient()

    render(<NotificationSettings api={api} />)

    fireEvent.click(await screen.findByLabelText('Do not disturb'))
    fireEvent.change(screen.getByLabelText('From'), { target: { value: '22:00' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(api.updateNotificationPreferences).toHaveBeenCalledWith({
        dndStart: '22:00',
        dndEnd: '08:00',
        timeZone: 'UTC',
        digestAfterMinutes: 15,
      })
    })

    // Disabled again once saved, so a second click cannot re-send the same values.
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
    })
  })

  it('edits the digest threshold within the range the server accepts', async () => {
    const api = fakeApiClient()

    render(<NotificationSettings api={api} />)

    const digest = await screen.findByLabelText(/Batch a backlog/)

    // min and max mirror NotificationPreference's own constants, so the browser refuses what the
    // validator would refuse — before a round trip rather than after one.
    expect(digest).toHaveAttribute('min', '1')
    expect(digest).toHaveAttribute('max', '1440')

    fireEvent.change(digest, { target: { value: '60' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => {
      expect(api.updateNotificationPreferences).toHaveBeenCalledWith(
        expect.objectContaining({ digestAfterMinutes: 60 }) as unknown,
      )
    })
  })

  it('reports a refused save and keeps the draft', async () => {
    const api = fakeApiClient({
      updateNotificationPreferences: vi.fn(() => Promise.reject(new Error('The API returned 400.'))),
    })

    render(<NotificationSettings api={api} />)

    fireEvent.click(await screen.findByLabelText('Do not disturb'))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The API returned 400.')

    // The draft survives, so the person can correct it rather than re-enter it.
    expect(screen.getByLabelText('Do not disturb')).toBeChecked()
    expect(screen.getByRole('button', { name: 'Save' })).toBeEnabled()
  })

  it('says it is saving while the request is in flight', async () => {
    const api = fakeApiClient({
      updateNotificationPreferences: vi.fn(() => pending<NotificationPreferences>()),
    })

    render(<NotificationSettings api={api} />)

    fireEvent.click(await screen.findByLabelText('Do not disturb'))
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    const saving = await screen.findByRole('button', { name: 'Saving…' })

    expect(saving).toBeDisabled()
  })

  it('names its section for assistive technology', async () => {
    render(<NotificationSettings api={fakeApiClient()} />)

    await screen.findByLabelText('Do not disturb')

    expect(screen.getByRole('region', { name: 'Notification settings' })).toBeInTheDocument()
  })
})
