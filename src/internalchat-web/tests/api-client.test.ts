import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { ApiError, createApiClient } from '../src/lib/api/client'
import { at } from './at'

/**
 * The API client: token attachment, refusal, and URL construction.
 *
 * What is worth asserting here is narrow, because the client is deliberately narrow. It does not
 * retry, cache, or interpret errors — a client that did any of those would be a second place where
 * access decisions appear to be made. So these tests assert the three things it does do, and one
 * thing it must never do: send a request without a token.
 */

const originalFetch = globalThis.fetch

/** A fetch target as a string. `String()` on a Request would give '[object Object]'. */
function urlOf(target: string | URL | Request): string {
  return target instanceof Request ? target.url : String(target)
}

/** The body a request carried, which in these tests is always the JSON the client built. */
function bodyOf(init: RequestInit | undefined): string {
  const body = init?.body

  if (typeof body !== 'string') {
    throw new Error(`Expected a string request body, got ${typeof body}.`)
  }

  return body
}

/** Records what fetch was called with and answers with a canned response. */
function stubFetch(response: Partial<Response> & { status?: number } = {}) {
  const calls: { url: string; init: RequestInit | undefined }[] = []

  const fetchMock = vi.fn((url: string | URL | Request, init?: RequestInit) => {
    calls.push({ url: urlOf(url), init })

    const status = response.status ?? 200

    return Promise.resolve({
      ok: status >= 200 && status < 300,
      status,
      json: () => Promise.resolve(response.json ? response.json() : { ok: true }),
      ...response,
    } as Response)
  })

  globalThis.fetch = fetchMock

  return { calls, fetchMock }
}

beforeEach(() => {
  vi.restoreAllMocks()
})

afterEach(() => {
  globalThis.fetch = originalFetch
})

describe('authorization', () => {
  it('attaches the bearer token to every request', async () => {
    const { calls } = stubFetch()

    await createApiClient('https://api.test', () => Promise.resolve('token-abc')).getMe()

    const headers = new Headers(at(calls, 0).init?.headers)

    expect(headers.get('Authorization')).toBe('Bearer token-abc')
    expect(headers.get('Accept')).toBe('application/json')
  })

  it('refuses rather than sending a request with no token', async () => {
    const { fetchMock } = stubFetch()

    const client = createApiClient('https://api.test', () => Promise.resolve(null))

    // Refused here rather than sent unauthenticated. An unauthenticated request comes back 401 and
    // is indistinguishable from a revoked token — a materially different situation, and the one the
    // sign-in flow needs to tell apart.
    await expect(client.getMe()).rejects.toThrow(ApiError)
    await expect(client.getSessions()).rejects.toMatchObject({ status: 401 })
    await expect(client.revokeSession('s1')).rejects.toMatchObject({ status: 401 })
    await expect(client.authorized('/anything')).rejects.toMatchObject({ status: 401 })

    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('preserves caller headers supplied as an array of pairs', async () => {
    const { calls } = stubFetch()

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    await client.authorized('/x', { headers: [['X-Trace', 'abc']] })

    // Headers are built through the Headers constructor rather than spread into an object literal.
    // Spreading an array of pairs produces numeric keys and silently drops every header — including
    // the Authorization one added afterwards.
    const headers = new Headers(at(calls, 0).init?.headers)

    expect(headers.get('X-Trace')).toBe('abc')
    expect(headers.get('Authorization')).toBe('Bearer t')
  })
})

describe('failure', () => {
  it('turns a non-2xx response into an ApiError carrying the status', async () => {
    stubFetch({ status: 403 })

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    await expect(client.getMe()).rejects.toMatchObject({ name: 'ApiError', status: 403 })
  })

  it('reports a failed no-content request too', async () => {
    stubFetch({ status: 404 })

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    // requestNoContent never parses a body, so without this check a 404 would resolve successfully
    // and the caller would believe the session was revoked.
    await expect(client.revokeSession('s1')).rejects.toMatchObject({ status: 404 })
  })

  it('hands back the raw response from authorized, including a failing one', async () => {
    stubFetch({ status: 409 })

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    // Deliberately not thrown: the messaging client needs the STATUS, because FR-011 distinguishes
    // a newly created message (201) from a replay of an already-accepted key (200).
    const response = await client.authorized('/x')

    expect(response.status).toBe(409)
  })

  it('carries a readable message', () => {
    const error = new ApiError(507, 'Attachment storage is full.')

    expect(error.name).toBe('ApiError')
    expect(error.status).toBe(507)
    expect(error.message).toBe('Attachment storage is full.')
    expect(error).toBeInstanceOf(Error)
  })
})

describe('endpoints', () => {
  it('reads the current employee', async () => {
    const { calls } = stubFetch({
      json: () =>
        Promise.resolve({
          id: 'e1',
          displayName: 'An Nguyen',
          email: 'an@example.test',
          avatarUrl: null,
          isAdmin: false,
          canReceiveNotifications: false,
          notificationBlockReason: 'denied',
        }),
    })

    const me = await createApiClient('https://api.test', () => Promise.resolve('t')).getMe()

    expect(at(calls, 0).url).toBe('https://api.test/me')
    expect(me.displayName).toBe('An Nguyen')

    // FR-040: the client is told *why* it cannot notify, not just that it cannot.
    expect(me.notificationBlockReason).toBe('denied')
  })

  it('lists and revokes sessions', async () => {
    const { calls } = stubFetch({ json: () => Promise.resolve([]) })

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    await client.getSessions()
    await client.revokeSession('session with spaces/and-slashes')

    expect(at(calls, 0).url).toBe('https://api.test/me/sessions')

    // Encoded, so a session id containing a slash cannot address a different path.
    expect(at(calls, 1).url).toBe(
      'https://api.test/me/sessions/session%20with%20spaces%2Fand-slashes',
    )
    expect(at(calls, 1).init?.method).toBe('DELETE')
  })

  it('encodes a directory query rather than interpolating it', async () => {
    const { calls } = stubFetch({ json: () => Promise.resolve([]) })

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    await client.searchEmployees('a&b=c', 5)

    // Unencoded, "a&b=c" would arrive as two parameters and a corrupted limit.
    expect(at(calls, 0).url).toBe('https://api.test/directory/employees?q=a%26b%3Dc&limit=5')
  })

  it('defaults the directory limit', async () => {
    const { calls } = stubFetch({ json: () => Promise.resolve([]) })

    await createApiClient('https://api.test', () => Promise.resolve('t')).searchEmployees('an')

    expect(at(calls, 0).url).toContain('limit=20')
  })

  it('fetches the VAPID public key', async () => {
    const { calls } = stubFetch({ json: () => Promise.resolve({ publicKey: 'BKey' }) })

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    // Not a secret — it is the key a browser needs to create a subscription at all (FR-034).
    await expect(client.getVapidPublicKey()).resolves.toEqual({ publicKey: 'BKey' })
    expect(at(calls, 0).url).toBe('https://api.test/notifications/vapid-public-key')
  })

  it('reads and replaces notification preferences', async () => {
    const preferences = {
      dndStart: '22:00',
      dndEnd: '07:00',
      timeZone: 'Asia/Ho_Chi_Minh',
      digestAfterMinutes: 15,
    }

    const { calls } = stubFetch({ json: () => Promise.resolve(preferences) })

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    await client.getNotificationPreferences()
    await client.updateNotificationPreferences(preferences)

    expect(at(calls, 0).init?.method).toBeUndefined()
    expect(at(calls, 1).init?.method).toBe('PUT')
    expect(JSON.parse(bodyOf(at(calls, 1).init))).toEqual(preferences)
  })

  it('registers a push subscription', async () => {
    const { calls } = stubFetch({ status: 204 })

    const client = createApiClient('https://api.test', () => Promise.resolve('t'))

    await client.registerPushSubscription({
      endpoint: 'https://push.example.test/abc',
      p256dh: 'key',
      auth: 'secret',
    })

    expect(at(calls, 0).url).toBe('https://api.test/notifications/subscriptions')
    expect(at(calls, 0).init?.method).toBe('POST')

    // 204 with no body. Parsing JSON here would throw on an empty response, which is why this path
    // exists separately from the one that parses.
    expect(JSON.parse(bodyOf(at(calls, 0).init))).toMatchObject({ auth: 'secret' })
  })
})
