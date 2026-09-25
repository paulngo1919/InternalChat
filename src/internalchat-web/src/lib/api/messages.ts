/**
 * Conversation and message endpoints (openapi.yaml).
 *
 * The types mirror the API's contract DTOs rather than the domain, which is the same boundary the
 * server enforces from its side — `tests/Architecture/BoundaryTests.cs` fails the build if a Domain
 * type reaches a contract, and duplicating the shape here is what keeps the wire format the only
 * thing the two share.
 */

import { ApiError, problemDetail, type EmployeeSummary } from './client'

/** `Conversation` in openapi.yaml. */
export interface ConversationResponse {
  readonly id: string
  readonly kind: 'direct' | 'group'
  readonly name: string | null
  readonly historyVisibility: 'from_join' | 'full'
  readonly lastSeq: number
  readonly unreadCount: number
  readonly memberCount: number
  readonly mutedUntil: string | null
  readonly lastMessage: MessageResponse | null
}

/** `Message` in openapi.yaml. `body` is null for a tombstone. */
export interface MessageResponse {
  readonly id: string
  readonly conversationId: string
  readonly seq: number
  readonly authorId: string
  readonly clientMessageKey: string
  readonly body: string | null
  readonly sentAt: string
  readonly editedAt: string | null
  readonly deletedAt: string | null
  readonly mentions: readonly string[]
  readonly attachments: readonly AttachmentResponse[]
}

/** `Attachment` in openapi.yaml. */
export interface AttachmentResponse {
  readonly id: string
  readonly kind: 'image' | 'video'
  readonly contentType: string
  readonly byteSize: number
  readonly durationSeconds: number | null
  readonly fileName: string
  readonly scanStatus: 'pending' | 'clean' | 'infected' | 'failed'
  /**
   * Present only when `scanStatus` is `clean`.
   *
   * A path on this API, never a signed storage URL — following it re-checks membership on every
   * request (FR-025). It is therefore not a capability: copying it somewhere else gives the
   * recipient nothing.
   */
  readonly contentUrl: string | null
  readonly posterUrl: string | null
}

/** One search result (`SearchResultPage.items` in openapi.yaml). */
export interface SearchResult {
  readonly messageId: string
  readonly conversationId: string
  readonly conversationName: string | null
  /** Position in its conversation — what jump-to-context navigates by (FR-031). */
  readonly seq: number
  /** A snippet with matches wrapped in `<mark>`, generated server-side. */
  readonly highlight: string
  readonly rank: number
}

/** `SearchResultPage` in openapi.yaml. */
export interface SearchResultPage {
  readonly items: readonly SearchResult[]
  readonly nextCursor: string | null
  /**
   * FR-033: the search was cut short to meet its budget.
   *
   * A client that ignores this tells its user there are no more results, which is the one wrong
   * answer — a truncated search and an exhaustive one are otherwise indistinguishable.
   */
  readonly truncated: boolean
}

/** `UploadTicket` in openapi.yaml. */
export interface UploadTicket {
  readonly attachmentId: string
  /** A one-time quarantine write location. Not a retrieval address. */
  readonly uploadUrl: string
  readonly expiresAt: string
}

/** `ConversationPage` in openapi.yaml. */
export interface ConversationPage {
  readonly items: readonly ConversationResponse[]
  readonly nextCursor: string | null
}

/** `MessagePage` in openapi.yaml. */
export interface MessagePage {
  readonly items: readonly MessageResponse[]
  readonly nextCursor: string | null
  readonly hasMore: boolean
}

/** `ReadState` in openapi.yaml — the effective position after the monotonic merge (FR-036). */
export interface ReadStateResponse {
  readonly conversationId: string
  readonly lastReadSeq: number
  readonly unreadCount: number
}

/** `Member` in openapi.yaml — one active member of a conversation (US3). */
export interface MemberResponse {
  readonly employee: EmployeeSummary
  readonly role: 'member' | 'admin'
  readonly joinedAt: string
}

/** What a send returned, and whether it was a replay. */
export interface SendResult {
  readonly message: MessageResponse
  /**
   * True when the server answered 200 rather than 201 — this key had already been accepted.
   *
   * The distinction is the whole point of FR-011's status codes: it tells the client its optimistic
   * bubble corresponds to an existing message rather than a newly created one, so it reconciles
   * instead of appending.
   */
  readonly wasReplay: boolean
}

/** `Meeting` in openapi.yaml. */
export interface MeetingResponse {
  readonly id: string
  readonly conversationId: string
  readonly startedBy: string
  readonly startedAt: string
  readonly endedAt: string | null
  readonly participantCount: number
  /**
   * The per-meeting ceiling (FR-042).
   *
   * Served rather than assumed, so a change to the limit does not need a frontend deployment to be
   * shown correctly.
   */
  readonly maxParticipants: number
}

/**
 * `MeetingToken` in openapi.yaml.
 *
 * <b>This one really is a bearer capability</b>, unlike an attachment's content URL: the media
 * server accepts it on its face and cannot re-check membership. Hence short-lived, and hence
 * `expiresAt` being served — a client that knows when it expires can ask for another rather than
 * being disconnected mid-sentence.
 */
export interface MeetingTokenResponse {
  readonly token: string
  readonly mediaServerUrl: string
  readonly expiresAt: string
}

/** The outcome of claiming the single screen-share slot (FR-050). */
export interface ScreenShareResponse {
  readonly meetingId: string
  readonly employeeId: string
  readonly scope: 'screen' | 'window'
  readonly startedAt: string
  /** Whose share this one stopped, or null when the slot was free. */
  readonly displacedEmployeeId: string | null
}

/** A request that carries a bearer token. */
export type Authorized = (path: string, init?: RequestInit) => Promise<Response>

/** Builds the messaging client over an authorized fetch. */
export function createMessagingClient(request: Authorized) {
  async function json<T>(path: string, init?: RequestInit): Promise<T> {
    const response = await request(path, init)

    if (!response.ok) {
      throw new ApiError(response.status, `The API returned ${String(response.status)}.`)
    }

    return (await response.json()) as T
  }

  return {
    listConversations: (cursor?: string, limit = 50) =>
      json<ConversationPage>(
        `/conversations?limit=${String(limit)}${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`,
      ),

    getConversation: (conversationId: string) =>
      json<ConversationResponse>(`/conversations/${conversationId}`),

    createDirectConversation: (employeeId: string) =>
      json<ConversationResponse>('/conversations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ kind: 'direct', memberIds: [employeeId] }),
      }),

    /** Creates a named group (US3 scenario 1, FR-008). The caller is added automatically. */
    createGroupConversation: (
      name: string,
      memberIds: readonly string[],
      historyVisibility: 'from_join' | 'full' = 'from_join',
    ) =>
      json<ConversationResponse>('/conversations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ kind: 'group', name, memberIds, historyVisibility }),
      }),

    /** The conversation's active members (US3). Removed memberships are not included. */
    listMembers: (conversationId: string) =>
      json<MemberResponse[]>(`/conversations/${conversationId}/members`),

    /**
     * Adds a member. Requires the caller to hold the conversation's admin role — the server refuses
     * anyone else, matching spec.md US1 scenario 5.
     */
    addMember: async (
      conversationId: string,
      employeeId: string,
      role?: 'member' | 'admin',
    ): Promise<void> => {
      const response = await request(`/conversations/${conversationId}/members`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ employeeId, role }),
      })

      if (!response.ok) {
        throw new ApiError(response.status, `The API returned ${String(response.status)}.`)
      }
    },

    /** Removes a member. Takes effect immediately for new messages (US3 scenario 3). */
    removeMember: async (conversationId: string, employeeId: string): Promise<void> => {
      const response = await request(`/conversations/${conversationId}/members/${employeeId}`, {
        method: 'DELETE',
      })

      if (!response.ok) {
        throw new ApiError(response.status, `The API returned ${String(response.status)}.`)
      }
    },

    /** Colleagues to add to a group, or to mention (FR-007, FR-015). */
    searchEmployees: (query: string, limit = 20) =>
      json<EmployeeSummary[]>(
        `/directory/employees?q=${encodeURIComponent(query)}&limit=${String(limit)}`,
      ),

    /** Advances the caller's read position for a conversation, monotonically (FR-036). */
    markRead: (conversationId: string, lastReadSeq: number) =>
      json<ReadStateResponse>(`/conversations/${conversationId}/read-state`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ lastReadSeq }),
      }),

    /** Mutes or unmutes a conversation for the caller (FR-037). Access is unaffected. */
    muteConversation: (conversationId: string, mutedUntil: string | null) =>
      json<{ mutedUntil: string | null }>(`/conversations/${conversationId}/mute`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mutedUntil }),
      }),

    /**
     * A page of history, newest first.
     *
     * Keyset, not offset: `beforeSeq` is the lowest sequence already held. `OFFSET` stays correct
     * only while nothing is inserted, and this is a conversation — something is always being
     * inserted, and the result would be a page that repeats or skips messages.
     */
    getHistory: (conversationId: string, beforeSeq?: number, limit = 50) =>
      json<MessagePage>(
        `/conversations/${conversationId}/messages?limit=${String(limit)}` +
          (beforeSeq === undefined ? '' : `&beforeSeq=${String(beforeSeq)}`),
      ),

    /**
     * Sends a message, reporting whether the server treated it as a replay.
     *
     * `clientMessageKey` comes from the caller — the offline queue owns it, and it must be the same
     * value on every attempt (FR-011).
     */
    sendMessage: async (
      conversationId: string,
      clientMessageKey: string,
      body: string,
      mentions?: readonly string[],
      attachmentIds?: readonly string[],
    ): Promise<SendResult> => {
      const response = await request(`/conversations/${conversationId}/messages`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        // Candidates only — the server resolves against active membership at send time (FR-015),
        // so naming someone who has left or was never a member costs nothing and notifies nobody.
        body: JSON.stringify({
          clientMessageKey,
          body,
          mentions: mentions ?? [],
          attachmentIds: attachmentIds ?? [],
        }),
      })

      if (!response.ok) {
        // The problem detail travels with the error so a refused message can say why (002 FR-004).
        throw new ApiError(
          response.status,
          `The API returned ${String(response.status)}.`,
          await problemDetail(response),
        )
      }

      return {
        message: (await response.json()) as MessageResponse,
        wasReplay: response.status === 200,
      }
    },

    /**
     * Reserves an upload, which validates kind, type, size, and duration before any bytes move.
     *
     * FR-023: the refusal arrives before the transfer, with the limit stated — so a 600 MB video is
     * rejected in a round trip rather than after a long upload.
     */
    requestUpload: (
      conversationId: string,
      request_: {
        kind: 'image' | 'video'
        contentType: string
        byteSize: number
        durationSeconds?: number | null
        fileName: string
      },
    ) =>
      json<UploadTicket>(`/conversations/${conversationId}/attachments`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request_),
      }),

    /**
     * Full-text search across the caller's own conversations (FR-029).
     *
     * Filters are passed through verbatim; the server intersects `conversationId` with what the
     * caller may read, so this client never has to know — or be trusted with — the scope.
     */
    searchMessages: (
      q: string,
      filters: {
        conversationId?: string
        authorId?: string
        from?: string
        to?: string
        hasAttachment?: 'any' | 'image' | 'video'
        cursor?: string
        limit?: number
      } = {},
    ) => {
      const params = new URLSearchParams({ q })

      // Listed explicitly rather than iterated. Object.entries would also forward any extra
      // property a caller happened to pass, and a filter name the server does not know is silently
      // ignored — which looks exactly like a filter that is not working.
      const entries: readonly (readonly [string, string | number | undefined])[] = [
        ['conversationId', filters.conversationId],
        ['authorId', filters.authorId],
        ['from', filters.from],
        ['to', filters.to],
        ['hasAttachment', filters.hasAttachment],
        ['cursor', filters.cursor],
        ['limit', filters.limit],
      ]

      for (const [key, value] of entries) {
        if (value !== undefined && value !== '') {
          params.set(key, String(value))
        }
      }

      return json<SearchResultPage>(`/search/messages?${params.toString()}`)
    },

    /** Polls an attachment's scan status, so the UI can swap "scanning…" for the image. */
    getAttachment: (attachmentId: string) =>
      json<AttachmentResponse>(`/attachments/${attachmentId}`),

    /**
     * Starts a meeting in a conversation (FR-041).
     *
     * Refused with 503 at the platform-wide ceiling (FR-044) or when the media host is
     * unreachable — and messaging is unaffected either way, which is the point of the separate
     * status: the client must not treat it as the platform being down.
     */
    startMeeting: (conversationId: string) =>
      json<MeetingResponse>(`/conversations/${conversationId}/meetings`, { method: 'POST' }),

    /**
     * Mints a join token.
     *
     * This call IS the meeting access control (FR-041): the media server trusts the token
     * completely, so membership is verified here and nowhere else.
     */
    joinMeeting: (meetingId: string) =>
      json<MeetingTokenResponse>(`/meetings/${meetingId}/token`, { method: 'POST' }),

    /** Claims the single screen-share slot, displacing whoever held it (FR-050). */
    startShare: (meetingId: string, scope: 'screen' | 'window') =>
      json<ScreenShareResponse>(`/meetings/${meetingId}/share`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ scope }),
      }),

    /** Releases the slot. */
    stopShare: async (meetingId: string): Promise<void> => {
      const response = await request(`/meetings/${meetingId}/share`, { method: 'DELETE' })

      if (!response.ok) {
        throw new ApiError(response.status, `The API returned ${String(response.status)}.`)
      }
    },

    editMessage: (conversationId: string, messageId: string, body: string) =>
      json<MessageResponse>(`/conversations/${conversationId}/messages/${messageId}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ body }),
      }),

    deleteMessage: async (conversationId: string, messageId: string): Promise<void> => {
      const response = await request(`/conversations/${conversationId}/messages/${messageId}`, {
        method: 'DELETE',
      })

      if (!response.ok) {
        throw new ApiError(response.status, `The API returned ${String(response.status)}.`)
      }
    },
  }
}

/** The messaging client type, for components that receive one. */
export type MessagingClient = ReturnType<typeof createMessagingClient>
