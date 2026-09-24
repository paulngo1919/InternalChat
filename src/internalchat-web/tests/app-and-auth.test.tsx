import { cleanup, fireEvent, render, renderHook, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { AuthContext, useAuth } from '../src/lib/auth/authContext'
import { fakeAuth, renderWithProviders } from './helpers'
import { at } from './at'

/**
 * The session provider, the protected-route wrapper, and the signed-in screen.
 *
 * <b>`signOut` going through the end-session endpoint is the assertion worth having.</b> Dropping
 * the local tokens looks identical from inside the app — the screen returns to the sign-in prompt
 * either way — but it leaves the session open at Keycloak, so the next sign-in completes silently
 * and an employee who signed out on a shared machine did not. It is also what triggers the
 * back-channel logout that writes this platform's revocation set, so skipping it leaves the
 * server believing the session is live.
 *
 * `oidcClient` is stubbed rather than driven through a real redirect: jsdom cannot navigate, and
 * `tests/auth-flow.test.ts` already covers what those functions put in the URL.
 */

const oidc = {
  completeSignIn: vi.fn((_config: unknown, _url: string) => Promise.resolve<unknown>(null)),
  beginSignIn: vi.fn((_config: unknown, _returnTo?: string) => Promise.resolve()),
  signOut: vi.fn((_config: unknown, _idToken: string | null) => {
    /* records the call */
  }),
  takeReturnTo: vi.fn(() => null as string | null),
}

vi.mock('../src/lib/auth/oidcClient', () => ({
  completeSignIn: (config: unknown, url: string) => oidc.completeSignIn(config, url),
  beginSignIn: (config: unknown, returnTo?: string) => oidc.beginSignIn(config, returnTo),
  signOut: (config: unknown, idToken: string | null) => {
    oidc.signOut(config, idToken)
  },
  takeReturnTo: () => oidc.takeReturnTo(),
  SignInError: class extends Error {},
}))

/**
 * App mounts ChatShell, which owns a real `ChatConnection`.
 *
 * Left unmocked it builds a SignalR connection in jsdom, which throws during render and takes the
 * whole tree down with it — so every assertion below would fail against an empty document for a
 * reason that has nothing to do with what is being tested. `tests/chat-connection.test.ts` covers
 * the real one.
 */
vi.mock('../src/lib/realtime/chatConnection', () => ({
  ChatEvents: {},
  ChatConnection: class {
    start() {
      return Promise.resolve()
    }

    stop() {
      return Promise.resolve()
    }

    startTyping() {
      return Promise.resolve()
    }

    stopTyping() {
      return Promise.resolve()
    }
  },
}))

/** jsdom has no layout, so a real Virtuoso measures nothing and renders nothing. */
vi.mock('react-virtuoso', () => ({
  Virtuoso: () => <div data-testid="virtuoso" />,
}))

const refresher = {
  started: [] as unknown[],
  stopped: 0,
  idToken: 'the-id-token' as string | null,
  onTokens: null as (() => void) | null,
  onSessionLost: null as ((reason: string) => void) | null,
}

vi.mock('../src/lib/auth/TokenRefresh', () => ({
  TokenRefresh: class {
    constructor(
      _config: unknown,
      handlers: { onTokens: () => void; onSessionLost: (reason: string) => void },
    ) {
      refresher.onTokens = handlers.onTokens
      refresher.onSessionLost = handlers.onSessionLost
    }

    get idToken() {
      return refresher.idToken
    }

    start(tokens: unknown) {
      refresher.started.push(tokens)
      refresher.onTokens?.()
    }

    stop() {
      refresher.stopped += 1
    }

    getValidAccessToken() {
      return Promise.resolve('token')
    }
  },
}))

const { AuthProvider, RequireAuth } = await import('../src/lib/auth/AuthProvider')

const config = {
  authority: 'https://keycloak.test/realms/internalchat',
  clientId: 'internalchat-web',
  redirectUri: 'https://chat.test/',
  postLogoutRedirectUri: 'https://chat.test/',
  scope: 'openid profile email',
}

beforeEach(() => {
  oidc.completeSignIn.mockResolvedValue(null)
  oidc.takeReturnTo.mockReturnValue(null)
  oidc.beginSignIn.mockClear()
  oidc.signOut.mockClear()

  refresher.started = []
  refresher.stopped = 0
  refresher.idToken = 'the-id-token'

  vi.spyOn(globalThis.history, 'replaceState').mockImplementation(() => undefined)
})

afterEach(() => {
  vi.restoreAllMocks()
  cleanup()
})

describe('useAuth', () => {
  it('throws outside the provider rather than looking signed out', () => {
    // A signed-out-looking default would make a mis-mounted component render the sign-in prompt
    // forever, which looks like an auth bug and is a tree-shape one.
    expect(() => renderHook(() => useAuth())).toThrow(/inside <AuthProvider>/)
  })

  it('returns the session inside one', () => {
    const state = fakeAuth()

    const { result } = renderHook(() => useAuth(), {
      wrapper: ({ children }) => (
        <AuthContext.Provider value={state}>{children}</AuthContext.Provider>
      ),
    })

    expect(result.current).toBe(state)
  })
})

describe('AuthProvider', () => {
  function renderProvider() {
    return render(
      <AuthProvider config={config}>
        <Probe />
      </AuthProvider>,
    )
  }

  function Probe() {
    const { status, error, signIn, signOut } = useAuth()

    return (
      <div>
        <span data-testid="status">{status}</span>
        <span data-testid="error">{error ?? ''}</span>
        <button type="button" onClick={signIn}>
          In
        </button>
        <button type="button" onClick={signOut}>
          Out
        </button>
      </div>
    )
  }

  it('settles as signed out when the URL carries no authorization code', async () => {
    renderProvider()

    // The ordinary case for every navigation that is not the redirect back from Keycloak.
    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('signed-out')
    })
  })

  it('starts the refresher and restores the path after a successful redirect', async () => {
    oidc.completeSignIn.mockResolvedValue({ accessToken: 'a', refreshToken: 'r', idToken: 'i' })
    oidc.takeReturnTo.mockReturnValue('/conversations/c1')

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('signed-in')
    })

    expect(refresher.started).toHaveLength(1)

    // The code is single-use and already redeemed. Leaving it in the address bar means a refresh
    // replays a dead code and shows an error on a working session — and keeps it in history.
    expect(globalThis.history.replaceState).toHaveBeenCalledWith({}, '', '/conversations/c1')
  })

  it('falls back to the root when there is no path to restore', async () => {
    oidc.completeSignIn.mockResolvedValue({ accessToken: 'a', refreshToken: 'r', idToken: 'i' })

    renderProvider()

    await waitFor(() => {
      expect(globalThis.history.replaceState).toHaveBeenCalledWith({}, '', '/')
    })
  })

  it('surfaces a failed sign-in with its reason', async () => {
    oidc.completeSignIn.mockRejectedValue(new Error('The sign-in response did not match.'))

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('error')
    })

    expect(screen.getByTestId('error')).toHaveTextContent('The sign-in response did not match.')
  })

  it('reports a non-Error failure without crashing', async () => {
    oidc.completeSignIn.mockRejectedValue('something odd')

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('error')).toHaveTextContent('Sign-in failed.')
    })
  })

  it('signs out through the end-session endpoint, with the id token hint', async () => {
    oidc.completeSignIn.mockResolvedValue({ accessToken: 'a', refreshToken: 'r', idToken: 'i' })

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('signed-in')
    })

    fireEvent.click(screen.getByRole('button', { name: 'Out' }))

    // Clearing tokens locally would leave the Keycloak session open, so the next sign-in completes
    // silently — and an employee who signed out on a shared machine did not.
    expect(oidc.signOut).toHaveBeenCalledWith(config, 'the-id-token')
    expect(refresher.stopped).toBeGreaterThan(0)
  })

  it('starts a sign-in from the current path', async () => {
    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('signed-out')
    })

    fireEvent.click(screen.getByRole('button', { name: 'In' }))

    // So someone who deep-linked lands where they meant to rather than on the conversation list.
    expect(oidc.beginSignIn).toHaveBeenCalledWith(config, globalThis.location.pathname)
  })

  it('reports the session lost when the refresher says so', async () => {
    oidc.completeSignIn.mockResolvedValue({ accessToken: 'a', refreshToken: 'r', idToken: 'i' })

    renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('signed-in')
    })

    refresher.onSessionLost?.('The refresh token was rejected.')

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('signed-out')
    })

    expect(screen.getByTestId('error')).toHaveTextContent('The refresh token was rejected.')
  })

  it('stops the refresher on unmount', async () => {
    const { unmount } = renderProvider()

    await waitFor(() => {
      expect(screen.getByTestId('status')).toHaveTextContent('signed-out')
    })

    unmount()

    // A scheduled refresh outliving the tree is a timer firing against a dead component.
    expect(refresher.stopped).toBeGreaterThan(0)
  })
})

describe('RequireAuth', () => {
  function renderGuard(overrides: Parameters<typeof fakeAuth>[0] = {}, fallback?: React.ReactNode) {
    const auth = fakeAuth(overrides)

    render(
      <AuthContext.Provider value={auth}>
        <RequireAuth fallback={fallback}>
          <p>secret</p>
        </RequireAuth>
      </AuthContext.Provider>,
    )

    return auth
  }

  it('renders its children only for a signed-in employee', () => {
    renderGuard({ status: 'signed-in' })

    expect(screen.getByText('secret')).toBeInTheDocument()
  })

  it('shows a fallback while the session is being established', () => {
    renderGuard({ status: 'starting' })

    expect(screen.queryByText('secret')).not.toBeInTheDocument()
    expect(screen.getByText('Signing in…')).toBeInTheDocument()

    cleanup()
    renderGuard({ status: 'starting' }, <p>Hold on</p>)

    expect(screen.getByText('Hold on')).toBeInTheDocument()
  })

  it('starts a sign-in when signed out, from an effect rather than during render', () => {
    const auth = renderGuard({ status: 'signed-out' })

    // Navigating away mid-render is a side effect in a place React is entitled to run twice.
    expect(auth.signIn).toHaveBeenCalled()
    expect(screen.queryByText('secret')).not.toBeInTheDocument()
  })

  it('offers a retry after a failed sign-in, and never the children', () => {
    const auth = renderGuard({ status: 'error', error: 'Authorization failed: access_denied' })

    expect(screen.getByRole('alert')).toHaveTextContent('Authorization failed: access_denied')
    expect(screen.queryByText('secret')).not.toBeInTheDocument()

    // Not retried automatically: a sign-in that failed because the employee was deactivated would
    // otherwise redirect in a loop.
    expect(auth.signIn).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }))

    expect(auth.signIn).toHaveBeenCalled()
  })
})

describe('App', () => {
  /** Routes the real fetch the API client uses, since App builds its own client. */
  function stubApi(routes: Record<string, unknown>, failures: Record<string, number> = {}) {
    const calls: { url: string; method: string }[] = []

    vi.stubGlobal(
      'fetch',
      vi.fn((url: string | URL | Request, init?: RequestInit) => {
        const path = url instanceof Request ? url.url : String(url)

        calls.push({ url: path, method: init?.method ?? 'GET' })

        const failure = Object.keys(failures).find((prefix) => path.includes(prefix))

        if (failure !== undefined) {
          return Promise.resolve({
            ok: false,
            status: failures[failure],
            json: () => Promise.resolve({}),
          } as Response)
        }

        const key = Object.keys(routes).find((prefix) => path.includes(prefix))

        return Promise.resolve({
          ok: true,
          status: 200,
          json: () => Promise.resolve(key === undefined ? {} : routes[key]),
        } as Response)
      }),
    )

    return calls
  }

  const me = {
    id: 'e1',
    displayName: 'An Nguyen',
    email: 'an@example.test',
    avatarUrl: null,
    isAdmin: false,
    canReceiveNotifications: true,
    notificationBlockReason: null,
  }

  const sessions = [
    {
      id: 's1',
      userAgent: 'Firefox on Linux',
      createdAt: '2026-09-01T09:00:00Z',
      lastSeenAt: '2026-09-22T09:00:00Z',
      isCurrent: true,
    },
    {
      id: 's2',
      userAgent: '',
      createdAt: '2026-09-01T09:00:00Z',
      lastSeenAt: '2026-09-20T09:00:00Z',
      isCurrent: false,
    },
  ]

  /** The order matters: '/me/sessions' has to be matched before '/me'. */
  const defaultRoutes = {
    '/me/sessions': sessions,
    '/notifications/preferences': {
      dndStart: null,
      dndEnd: null,
      timeZone: 'UTC',
      digestAfterMinutes: 15,
    },
    '/conversations': { items: [], nextCursor: null },
    '/me': me,
  }

  async function renderApp(
    routes: Record<string, unknown> = defaultRoutes,
    failures: Record<string, number> = {},
  ) {
    const App = (await import('../src/App')).default
    const calls = stubApi(routes, failures)
    const auth = fakeAuth()

    renderWithProviders(
      <AuthContext.Provider value={auth}>
        <App />
      </AuthContext.Provider>,
    )

    return { auth, calls }
  }

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('shows the profile once it loads', async () => {
    await renderApp()

    expect(await screen.findByTestId('display-name')).toHaveTextContent('An Nguyen')
    expect(screen.getByText('an@example.test')).toBeInTheDocument()
  })

  it('says it is loading before the profile arrives', async () => {
    await renderApp()

    // Rendered synchronously, before any await resolves.
    expect(screen.getByText(/loading your profile/i)).toBeInTheDocument()
  })

  it('reports a failed profile load as an alert and renders nothing else', async () => {
    await renderApp(defaultRoutes, { '/me': 503 })

    // The whole screen depends on knowing who you are. Rendering a chat shell for an unknown
    // employee would ask the server for conversations on behalf of nobody.
    expect(await screen.findByRole('alert')).toHaveTextContent('503')
    expect(screen.queryByTestId('display-name')).not.toBeInTheDocument()
  })

  it('lists the signed-in devices, marking the current one', async () => {
    await renderApp()

    const devices = await screen.findAllByTestId('session')

    expect(devices).toHaveLength(2)
    expect(at(devices, 0)).toHaveTextContent('Firefox on Linux')
    expect(at(devices, 0)).toHaveTextContent('(this device)')

    // A session whose user agent the server never captured. An empty row gives the person nothing
    // to recognise, and this is the list they use to spot a device that is not theirs.
    expect(at(devices, 1)).toHaveTextContent('Unknown device')
    expect(at(devices, 1)).not.toHaveTextContent('(this device)')
  })

  it('revokes a device and refreshes the list', async () => {
    const { calls } = await renderApp()

    await screen.findAllByTestId('session')

    fireEvent.click(at(screen.getAllByRole('button', { name: 'Sign this device out' }), 0))

    await waitFor(() => {
      expect(calls.some((call) => call.method === 'DELETE' && call.url.endsWith('/me/sessions/s1'))).toBe(
        true,
      )
    })

    // Refetched rather than spliced out locally: FR-005's list is only useful if it reflects what
    // the server actually did.
    await waitFor(() => {
      expect(calls.filter((call) => call.url.endsWith('/me/sessions')).length).toBeGreaterThan(1)
    })
  })

  it('signs out through the session, not by clearing anything itself', async () => {
    const { auth } = await renderApp()

    await screen.findByTestId('display-name')

    fireEvent.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(auth.signOut).toHaveBeenCalled()
  })

  it('says nothing about notifications when this browser is already covered', async () => {
    await renderApp()

    await screen.findByTestId('display-name')

    expect(screen.queryByTestId('notification-capability-warning')).not.toBeInTheDocument()
  })

  it('warns when this browser cannot be reached', async () => {
    await renderApp({ ...defaultRoutes, '/me': { ...me, canReceiveNotifications: false } })

    // FR-040. The specific wording depends on what this browser supports, which
    // `tests/notifications.test.tsx` covers — here it only has to appear at all.
    expect(await screen.findByTestId('notification-capability-warning')).toBeInTheDocument()
  })

  it('renders the messaging screen and the notification settings', async () => {
    await renderApp()

    await screen.findByTestId('display-name')

    expect(await screen.findByTestId('no-conversations')).toBeInTheDocument()
    expect(
      await screen.findByRole('region', { name: 'Notification settings' }),
    ).toBeInTheDocument()
  })
})
