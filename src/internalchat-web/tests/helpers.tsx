import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import type { ReactElement, ReactNode } from 'react'
import { vi } from 'vitest'

import type { MessagingClient } from '../src/lib/api/messages'
import type { ApiClient } from '../src/lib/api/client'
import type { AuthState } from '../src/lib/auth/authContext'
import { AuthContext } from '../src/lib/auth/authContext'

/**
 * Shared scaffolding for component tests.
 *
 * Not a test file — `vitest.config.ts` collects only `*.test.{ts,tsx}`, so this is imported rather
 * than run.
 */

/**
 * A query client configured for tests.
 *
 * <b>Retries off is the setting that matters.</b> TanStack Query's default retries a failed query
 * three times with backoff, so a test asserting an error state would wait several seconds and then
 * fail on a timeout rather than on the assertion — which reads as a flaky test rather than a broken
 * component. `staleTime: Infinity` stops a refetch firing mid-assertion.
 */
export function newQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, staleTime: Infinity, gcTime: Infinity },
      mutations: { retry: false },
    },
  })
}

/** Renders inside a query client, and a session when one is given. */
export function renderWithProviders(
  ui: ReactElement,
  options: { auth?: Partial<AuthState>; queryClient?: QueryClient } = {},
) {
  const queryClient = options.queryClient ?? newQueryClient()

  function Wrapper({ children }: { children: ReactNode }) {
    const inner = <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>

    if (!options.auth) {
      return inner
    }

    return <AuthContext.Provider value={fakeAuth(options.auth)}>{inner}</AuthContext.Provider>
  }

  return { ...render(ui, { wrapper: Wrapper }), queryClient }
}

/** A session in whatever state the test needs. */
export function fakeAuth(overrides: Partial<AuthState> = {}): AuthState {
  return {
    status: 'signed-in',
    error: null,
    getAccessToken: () => Promise.resolve('token'),
    signIn: vi.fn(),
    signOut: vi.fn(),
    ...overrides,
  }
}

/** One message, with only the fields a test cares about overridden. */
export function aMessage(overrides: Partial<MessageResponseShape> = {}): MessageResponseShape {
  return {
    id: 'm1',
    conversationId: 'c1',
    seq: 1,
    authorId: 'e1',
    clientMessageKey: '01JBXQ7ZPT4M9WYFN2VKC3H6RD',
    body: 'hello',
    sentAt: '2026-09-22T09:00:00Z',
    editedAt: null,
    deletedAt: null,
    mentions: [],
    attachments: [],
    ...overrides,
  }
}

type MessageResponseShape = Awaited<ReturnType<MessagingClient['editMessage']>>

/** One conversation, with only the fields a test cares about overridden. */
export function aConversation(
  overrides: Partial<ConversationResponseShape> = {},
): ConversationResponseShape {
  return {
    id: 'c1',
    kind: 'group',
    name: 'Design',
    historyVisibility: 'from_join',
    lastSeq: 1,
    unreadCount: 0,
    memberCount: 3,
    mutedUntil: null,
    lastMessage: null,
    ...overrides,
  }
}

type ConversationResponseShape = Awaited<ReturnType<MessagingClient['getConversation']>>

/** One live meeting. */
export function aMeeting(overrides: Partial<MeetingShape> = {}): MeetingShape {
  return {
    id: 'meeting-1',
    conversationId: 'c1',
    startedBy: 'e1',
    startedAt: '2026-09-22T09:00:00Z',
    endedAt: null,
    participantCount: 1,
    maxParticipants: 25,
    ...overrides,
  }
}

type MeetingShape = Awaited<ReturnType<MessagingClient['startMeeting']>>

/**
 * A messaging client whose every method is a spy resolving to something harmless.
 *
 * Every method is stubbed rather than only the ones a given test uses, because a component that
 * calls an unstubbed method fails with "is not a function" from inside a render — several frames
 * away from the call, and hard to read.
 */
export function fakeMessagingClient(overrides: Partial<MessagingClient> = {}): MessagingClient {
  return {
    listConversations: vi.fn(() => Promise.resolve({ items: [], nextCursor: null })),
    getConversation: vi.fn(() => Promise.resolve(aConversation())),
    createDirectConversation: vi.fn(() => Promise.resolve(aConversation({ kind: 'direct' }))),
    createGroupConversation: vi.fn(() => Promise.resolve(aConversation())),
    listMembers: vi.fn(() => Promise.resolve([])),
    addMember: vi.fn(() => Promise.resolve()),
    removeMember: vi.fn(() => Promise.resolve()),
    searchEmployees: vi.fn(() => Promise.resolve([])),
    markRead: vi.fn(() =>
      Promise.resolve({ conversationId: 'c1', lastReadSeq: 1, unreadCount: 0 }),
    ),
    muteConversation: vi.fn(() => Promise.resolve({ mutedUntil: null })),
    getHistory: vi.fn(() => Promise.resolve({ items: [], nextCursor: null, hasMore: false })),
    sendMessage: vi.fn(() => Promise.resolve({ message: aMessage(), wasReplay: false })),
    requestUpload: vi.fn(() =>
      Promise.resolve({ attachmentId: 'a1', uploadUrl: 'https://minio.test/q/a1', expiresAt: 'x' }),
    ),
    searchMessages: vi.fn(() =>
      Promise.resolve({ items: [], nextCursor: null, truncated: false }),
    ),
    getAttachment: vi.fn(() =>
      Promise.resolve({
        id: 'a1',
        kind: 'image' as const,
        contentType: 'image/png',
        byteSize: 1024,
        durationSeconds: null,
        fileName: 'shot.png',
        scanStatus: 'clean' as const,
        contentUrl: '/api/v1/attachments/a1/content',
        posterUrl: null,
      }),
    ),
    startMeeting: vi.fn(() => Promise.resolve(aMeeting())),
    joinMeeting: vi.fn(() =>
      Promise.resolve({
        token: 'a-token',
        mediaServerUrl: 'wss://livekit.test',
        expiresAt: '2026-09-22T09:05:00Z',
      }),
    ),
    startShare: vi.fn(() =>
      Promise.resolve({
        meetingId: 'meeting-1',
        employeeId: 'e1',
        scope: 'screen' as const,
        startedAt: '2026-09-22T09:00:00Z',
        displacedEmployeeId: null,
      }),
    ),
    stopShare: vi.fn(() => Promise.resolve()),
    editMessage: vi.fn(() => Promise.resolve(aMessage({ editedAt: '2026-09-22T09:05:00Z' }))),
    deleteMessage: vi.fn(() => Promise.resolve()),
    ...overrides,
  }
}

/** An API client whose every method is a spy. */
export function fakeApiClient(overrides: Partial<ApiClient> = {}): ApiClient {
  return {
    authorized: vi.fn(() => Promise.resolve(new Response('{}', { status: 200 }))),
    getMe: vi.fn(() =>
      Promise.resolve({
        id: 'e1',
        displayName: 'An Nguyen',
        email: 'an@example.test',
        avatarUrl: null,
        isAdmin: false,
        canReceiveNotifications: true,
        notificationBlockReason: null,
      }),
    ),
    getSessions: vi.fn(() => Promise.resolve([])),
    revokeSession: vi.fn(() => Promise.resolve()),
    searchEmployees: vi.fn(() => Promise.resolve([])),
    getVapidPublicKey: vi.fn(() => Promise.resolve({ publicKey: 'BKey' })),
    getNotificationPreferences: vi.fn(() =>
      Promise.resolve({
        dndStart: null,
        dndEnd: null,
        timeZone: 'UTC',
        digestAfterMinutes: 15,
      }),
    ),
    updateNotificationPreferences: vi.fn((preferences) => Promise.resolve(preferences)),
    registerPushSubscription: vi.fn(() => Promise.resolve()),
    ...overrides,
  }
}
