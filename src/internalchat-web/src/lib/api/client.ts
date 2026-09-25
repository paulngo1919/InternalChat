/**
 * Thin API client. Attaches a valid bearer token to every request and nothing more.
 *
 * Deliberately small: the interesting behaviour of this platform is on the server, and a client
 * that retried, cached, or interpreted errors would be a second place where access decisions
 * appear to be made.
 */

/** Raised when the API refuses or fails a request. */
export class ApiError extends Error {
  /** HTTP status the API returned. */
  readonly status: number

  /**
   * The problem document's `detail`, when the API sent one — the reason worth showing a person, as
   * opposed to `message`, which only names the status (002 FR-004).
   */
  readonly detail: string | undefined

  constructor(status: number, message: string, detail?: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.detail = detail
  }
}

/** Reads `detail` from an RFC 7807 problem response, or nothing if the body is not one. */
export async function problemDetail(response: Response): Promise<string | undefined> {
  try {
    const problem = (await response.json()) as { detail?: unknown }
    return typeof problem.detail === 'string' && problem.detail.length > 0 ? problem.detail : undefined
  } catch {
    return undefined
  }
}

/** `GET /me` — the signed-in employee (openapi.yaml `CurrentEmployee`). */
export interface CurrentEmployee {
  readonly id: string
  readonly displayName: string
  readonly email: string
  readonly avatarUrl: string | null
  readonly isAdmin: boolean
  /** FR-040: false when this browser has no live push subscription. */
  readonly canReceiveNotifications: boolean
  readonly notificationBlockReason: string | null
}

/** `GET /me/sessions` — one active session (FR-005). */
export interface Session {
  readonly id: string
  readonly userAgent: string
  readonly createdAt: string
  readonly lastSeenAt: string
  readonly isCurrent: boolean
}

/** `NotificationPreferences` in openapi.yaml (FR-037, FR-038). `dndStart`/`dndEnd` are `"HH:mm"`. */
export interface NotificationPreferences {
  readonly dndStart: string | null
  readonly dndEnd: string | null
  readonly timeZone: string
  readonly digestAfterMinutes: number
}

/** `GET /directory/employees` — one search result (FR-007). */
export interface EmployeeSummary {
  readonly id: string
  readonly displayName: string
  readonly email: string
  readonly avatarUrl: string | null
  readonly status: 'active' | 'deactivated'
}

/** Creates a client bound to a base URL and a token source. */
export function createApiClient(baseUrl: string, getAccessToken: () => Promise<string | null>) {
  async function request<T>(path: string, init?: RequestInit): Promise<T> {
    const token = await getAccessToken()

    if (!token) {
      // Refused here rather than sent without one. An unauthenticated request would come back 401
      // and be indistinguishable from a revoked token, which is a materially different situation.
      throw new ApiError(401, 'Not signed in.')
    }

    // Built as a Headers instance rather than spread into an object literal: RequestInit.headers
    // may legitimately be an array of pairs, and spreading one of those into an object produces
    // numeric keys and silently drops every header.
    const headers = new Headers(init?.headers)
    headers.set('Authorization', `Bearer ${token}`)
    headers.set('Accept', 'application/json')

    const response = await fetch(`${baseUrl}${path}`, { ...init, headers })

    if (!response.ok) {
      throw new ApiError(response.status, `The API returned ${String(response.status)}.`)
    }

    return (await response.json()) as T
  }

  /** For endpoints that answer 204 with no body, where parsing JSON would throw. */
  async function requestNoContent(path: string, init?: RequestInit): Promise<void> {
    const token = await getAccessToken()

    if (!token) {
      throw new ApiError(401, 'Not signed in.')
    }

    const headers = new Headers(init?.headers)
    headers.set('Authorization', `Bearer ${token}`)

    const response = await fetch(`${baseUrl}${path}`, { ...init, headers })

    if (!response.ok) {
      throw new ApiError(response.status, `The API returned ${String(response.status)}.`)
    }
  }

  /**
   * Issues an authorized request and hands back the raw response.
   *
   * Exposed because the messaging client needs the STATUS, not just the body: FR-011 distinguishes a
   * newly created message (201) from a replay of a key already accepted (200), and a helper that
   * only returned parsed JSON would throw that distinction away — leaving the client unable to tell
   * its own retry from a new message.
   */
  async function authorized(path: string, init?: RequestInit): Promise<Response> {
    const token = await getAccessToken()

    if (!token) {
      throw new ApiError(401, 'Not signed in.')
    }

    const headers = new Headers(init?.headers)
    headers.set('Authorization', `Bearer ${token}`)
    headers.set('Accept', 'application/json')

    return fetch(`${baseUrl}${path}`, { ...init, headers })
  }

  return {
    authorized,
    getMe: () => request<CurrentEmployee>('/me'),
    getSessions: () => request<Session[]>('/me/sessions'),
    revokeSession: (sessionId: string) =>
      requestNoContent(`/me/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' }),
    searchEmployees: (query: string, limit = 20) =>
      request<EmployeeSummary[]>(
        `/directory/employees?q=${encodeURIComponent(query)}&limit=${String(limit)}`,
      ),

    /** The key a browser needs to create a push subscription (FR-034). Not a secret. */
    getVapidPublicKey: () => request<{ publicKey: string }>('/notifications/vapid-public-key'),

    /** Do-not-disturb window and digest threshold (FR-037, FR-038). */
    getNotificationPreferences: () =>
      request<NotificationPreferences>('/notifications/preferences'),

    /** Replaces the do-not-disturb window and digest threshold. */
    updateNotificationPreferences: (preferences: NotificationPreferences) =>
      request<NotificationPreferences>('/notifications/preferences', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(preferences),
      }),

    /** Registers this browser for push. Re-registering the same endpoint refreshes it. */
      registerPushSubscription: (subscription: {
      endpoint: string
      p256dh: string
      auth: string
    }): Promise<void> =>
      requestNoContent('/notifications/subscriptions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(subscription),
      }),
      
    /** Retrieves the current retention policy (FR-053). */
    getRetentionPolicy: () =>
      request<{ retentionMonths: number; appliesFrom: string; nextSweepAt: string }>('/admin/retention'),
  }
}

/** The client type, for components that receive one. */
export type ApiClient = ReturnType<typeof createApiClient>
