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

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
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

  return {
    getMe: () => request<CurrentEmployee>('/me'),
    getSessions: () => request<Session[]>('/me/sessions'),
    revokeSession: (sessionId: string) =>
      requestNoContent(`/me/sessions/${encodeURIComponent(sessionId)}`, { method: 'DELETE' }),
    searchEmployees: (query: string, limit = 20) =>
      request<EmployeeSummary[]>(
        `/directory/employees?q=${encodeURIComponent(query)}&limit=${String(limit)}`,
      ),
  }
}

/** The client type, for components that receive one. */
export type ApiClient = ReturnType<typeof createApiClient>
