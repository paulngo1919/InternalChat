import { describe, expect, it, vi } from 'vitest'

import { ApiError } from '../src/lib/api/client'
import { createMessagingClient, type Authorized } from '../src/lib/api/messages'
import { at } from './at'

/**
 * The messaging client — every conversation, message, member, search and attachment endpoint.
 *
 * This layer has no logic to speak of beyond URL construction and status interpretation, and both
 * of those are exactly where a silent defect lives. A filter appended under the wrong parameter
 * name is ignored by the server, which looks identical to a filter that is not working; a query
 * string built by interpolation rather than encoding turns a search for `a&b` into two parameters.
 * Neither produces an error anywhere.
 *
 * The one genuine piece of logic is `sendMessage`'s reading of 200 versus 201, and it gets the most
 * attention here: FR-011 makes that distinction the difference between the client appending a new
 * message and reconciling its own retry.
 */

/** A recording stand-in for the authorized fetch the client is built over. */
function stubRequest(...responses: { status: number; body?: unknown }[]) {
  const calls: { path: string; init: RequestInit | undefined }[] = []
  let index = 0

  const request: Authorized = vi.fn((path: string, init?: RequestInit) => {
    calls.push({ path, init })

    const response: { status: number; body?: unknown } =
      responses[Math.min(index, responses.length - 1)] ?? { status: 200 }
    index += 1

    return Promise.resolve({
      ok: response.status >= 200 && response.status < 300,
      status: response.status,
      json: () => Promise.resolve(response.body ?? {}),
    } as Response)
  })

  return { calls, request }
}

/**
 * The body of the nth call, parsed.
 *
 * Narrowed rather than stringified: `String()` on a stream body would give '[object Object]',
 * and every request this client makes carries JSON it built itself.
 */
function bodyOf(calls: { init: RequestInit | undefined }[], index = 0): unknown {
  const body = at(calls, index).init?.body

  if (typeof body !== 'string') {
    throw new Error(`Expected a string request body, got ${typeof body}.`)
  }

  return JSON.parse(body)
}

describe('conversations', () => {
  it('lists conversations with and without a cursor', async () => {
    const { calls, request } = stubRequest({ status: 200, body: { items: [], nextCursor: null } })
    const client = createMessagingClient(request)

    await client.listConversations()
    await client.listConversations('cursor/with+chars', 10)

    expect(at(calls, 0).path).toBe('/conversations?limit=50')

    // Encoded. A cursor is an opaque server token and may legitimately contain characters that
    // would otherwise terminate the parameter.
    expect(at(calls, 1).path).toBe('/conversations?limit=10&cursor=cursor%2Fwith%2Bchars')
  })

  it('reads one conversation', async () => {
    const { calls, request } = stubRequest({ status: 200, body: { id: 'c1' } })

    await createMessagingClient(request).getConversation('c1')

    expect(at(calls, 0).path).toBe('/conversations/c1')
  })

  it('creates a direct conversation naming only the other person', async () => {
    const { calls, request } = stubRequest({ status: 201, body: { id: 'c1' } })

    await createMessagingClient(request).createDirectConversation('e2')

    // The caller is added automatically by the server. Naming themselves here is how "a direct
    // conversation with myself" arrives, and the server's validator refuses it.
    expect(bodyOf(calls)).toEqual({ kind: 'direct', memberIds: ['e2'] })
  })

  it('creates a group, defaulting history to from_join', async () => {
    const { calls, request } = stubRequest({ status: 201, body: { id: 'c1' } })
    const client = createMessagingClient(request)

    await client.createGroupConversation('Design', ['e2', 'e3'])
    await client.createGroupConversation('Ops', ['e2'], 'full')

    // from_join is the safe default: a new member sees what was said after they arrived. Defaulting
    // to `full` would hand a joiner the whole backlog, which is a disclosure decision and not one
    // a client default should be making.
    expect(bodyOf(calls, 0)).toMatchObject({ historyVisibility: 'from_join', name: 'Design' })
    expect(bodyOf(calls, 1)).toMatchObject({ historyVisibility: 'full' })
  })

  it('mutes and unmutes without touching access', async () => {
    const { calls, request } = stubRequest({ status: 200, body: { mutedUntil: null } })
    const client = createMessagingClient(request)

    await client.muteConversation('c1', '2026-09-23T00:00:00Z')
    await client.muteConversation('c1', null)

    expect(at(calls, 0).path).toBe('/conversations/c1/mute')
    expect(bodyOf(calls, 0)).toEqual({ mutedUntil: '2026-09-23T00:00:00Z' })

    // null is the unmute, and it has to survive JSON.stringify as an explicit null rather than
    // being dropped as undefined — an absent field would leave the mute in place.
    expect(bodyOf(calls, 1)).toEqual({ mutedUntil: null })
  })
})

describe('members', () => {
  it('lists, adds and removes members', async () => {
    const { calls, request } = stubRequest({ status: 200, body: [] }, { status: 204 }, { status: 204 })
    const client = createMessagingClient(request)

    await client.listMembers('c1')
    await client.addMember('c1', 'e2', 'admin')
    await client.removeMember('c1', 'e2')

    expect(at(calls, 0).path).toBe('/conversations/c1/members')
    expect(bodyOf(calls, 1)).toEqual({ employeeId: 'e2', role: 'admin' })
    expect(at(calls, 2).path).toBe('/conversations/c1/members/e2')
    expect(at(calls, 2).init?.method).toBe('DELETE')
  })

  it('reports a refused add or remove rather than resolving', async () => {
    const { request } = stubRequest({ status: 403 })
    const client = createMessagingClient(request)

    // Adding a member requires the conversation's admin role. A silent resolve would leave the UI
    // showing someone as added who is not in the conversation at all.
    await expect(client.addMember('c1', 'e2')).rejects.toMatchObject({ status: 403 })
    await expect(client.removeMember('c1', 'e2')).rejects.toBeInstanceOf(ApiError)
  })

  it('omits the role when none is given', async () => {
    const { calls, request } = stubRequest({ status: 204 })

    await createMessagingClient(request).addMember('c1', 'e2')

    // undefined is dropped by JSON.stringify, so the server applies its own default rather than
    // receiving a null it would have to interpret.
    expect(bodyOf(calls)).toEqual({ employeeId: 'e2' })
  })
})

describe('history', () => {
  it('pages by keyset rather than offset', async () => {
    const { calls, request } = stubRequest({
      status: 200,
      body: { items: [], nextCursor: null, hasMore: false },
    })

    const client = createMessagingClient(request)

    await client.getHistory('c1')
    await client.getHistory('c1', 420, 25)

    expect(at(calls, 0).path).toBe('/conversations/c1/messages?limit=50')

    // beforeSeq, not offset. OFFSET stays correct only while nothing is inserted, and this is a
    // conversation — something is always being inserted, so an offset page repeats or skips.
    expect(at(calls, 1).path).toBe('/conversations/c1/messages?limit=25&beforeSeq=420')
  })

  it('treats beforeSeq of zero as a real value', async () => {
    const { calls, request } = stubRequest({
      status: 200,
      body: { items: [], nextCursor: null, hasMore: false },
    })

    await createMessagingClient(request).getHistory('c1', 0)

    // Checked against undefined rather than falsiness. Sequence zero is the beginning of a
    // conversation, and dropping it would silently page from the newest message instead.
    expect(at(calls, 0).path).toContain('beforeSeq=0')
  })

  it('marks read', async () => {
    const { calls, request } = stubRequest({
      status: 200,
      body: { conversationId: 'c1', lastReadSeq: 9, unreadCount: 0 },
    })

    await createMessagingClient(request).markRead('c1', 9)

    expect(at(calls, 0).init?.method).toBe('PUT')
    expect(bodyOf(calls)).toEqual({ lastReadSeq: 9 })
  })
})

describe('sending', () => {
  const sent = {
    id: 'm1',
    conversationId: 'c1',
    seq: 1,
    authorId: 'e1',
    clientMessageKey: 'key',
    body: 'hello',
    sentAt: '2026-09-22T09:00:00Z',
    editedAt: null,
    deletedAt: null,
    mentions: [],
    attachments: [],
  }

  it('reports 201 as a new message', async () => {
    const { request } = stubRequest({ status: 201, body: sent })

    const result = await createMessagingClient(request).sendMessage('c1', 'key', 'hello')

    expect(result.wasReplay).toBe(false)
    expect(result.message.id).toBe('m1')
  })

  it('reports 200 as a replay of a key the server already accepted', async () => {
    const { request } = stubRequest({ status: 200, body: sent })

    // FR-011. This is the whole reason the client keeps the raw Response: it tells the client its
    // optimistic bubble corresponds to an existing message, so it reconciles instead of appending
    // a duplicate the sender then sees twice.
    const result = await createMessagingClient(request).sendMessage('c1', 'key', 'hello')

    expect(result.wasReplay).toBe(true)
  })

  it('defaults mentions and attachments to empty arrays', async () => {
    const { calls, request } = stubRequest({ status: 201, body: sent })

    await createMessagingClient(request).sendMessage('c1', 'key', 'hello')

    // Sent as [] rather than omitted, so the server never has to distinguish "no mentions" from
    // "a client that does not support mentions".
    expect(bodyOf(calls)).toEqual({
      clientMessageKey: 'key',
      body: 'hello',
      mentions: [],
      attachmentIds: [],
    })
  })

  it('passes mentions through as candidates', async () => {
    const { calls, request } = stubRequest({ status: 201, body: sent })

    await createMessagingClient(request).sendMessage('c1', 'key', 'hi @an', ['e2', 'e-not-a-member'])

    // Candidates only — the server resolves them against active membership at send time (FR-015),
    // so naming someone who has left costs nothing and notifies nobody.
    expect(bodyOf(calls)).toMatchObject({ mentions: ['e2', 'e-not-a-member'] })
  })

  it('reports a refused send', async () => {
    const { request } = stubRequest({ status: 422 })

    await expect(
      createMessagingClient(request).sendMessage('c1', 'key', ''),
    ).rejects.toMatchObject({ status: 422 })
  })

  it('edits and deletes', async () => {
    const { calls, request } = stubRequest({ status: 200, body: sent }, { status: 204 })
    const client = createMessagingClient(request)

    await client.editMessage('c1', 'm1', 'corrected')
    await client.deleteMessage('c1', 'm1')

    expect(at(calls, 0).init?.method).toBe('PATCH')
    expect(bodyOf(calls, 0)).toEqual({ body: 'corrected' })
    expect(at(calls, 1).init?.method).toBe('DELETE')
  })

  it('reports a refused delete', async () => {
    const { request } = stubRequest({ status: 403 })

    // A delete outside the edit window, or by someone who is not the author. Resolving silently
    // would leave the message on screen as deleted while it is still visible to everyone else.
    await expect(
      createMessagingClient(request).deleteMessage('c1', 'm1'),
    ).rejects.toMatchObject({ status: 403 })
  })
})

describe('attachments', () => {
  it('reserves an upload before any bytes move', async () => {
    const { calls, request } = stubRequest({
      status: 201,
      body: { attachmentId: 'a1', uploadUrl: 'https://minio.test/q/a1', expiresAt: 'x' },
    })

    await createMessagingClient(request).requestUpload('c1', {
      kind: 'video',
      contentType: 'video/mp4',
      byteSize: 600_000_000,
      durationSeconds: 900,
      fileName: 'demo.mp4',
    })

    // FR-023: the refusal arrives before the transfer, with the limit stated — so a 600 MB video is
    // rejected in one round trip rather than after a long upload.
    expect(at(calls, 0).path).toBe('/conversations/c1/attachments')
    expect(bodyOf(calls)).toMatchObject({ byteSize: 600_000_000, durationSeconds: 900 })
  })

  it('polls one attachment', async () => {
    const { calls, request } = stubRequest({ status: 200, body: { id: 'a1', scanStatus: 'clean' } })

    await createMessagingClient(request).getAttachment('a1')

    expect(at(calls, 0).path).toBe('/attachments/a1')
  })
})

describe('search', () => {
  it('sends only the filters that were given', async () => {
    const { calls, request } = stubRequest({
      status: 200,
      body: { items: [], nextCursor: null, truncated: false },
    })

    await createMessagingClient(request).searchMessages('quarterly report', {
      conversationId: 'c1',
      hasAttachment: 'image',
      limit: 25,
    })

    const query = new URLSearchParams(at(at(calls, 0).path.split('?'), 1))

    expect(query.get('q')).toBe('quarterly report')
    expect(query.get('conversationId')).toBe('c1')
    expect(query.get('hasAttachment')).toBe('image')
    expect(query.get('limit')).toBe('25')

    // Absent filters are absent, not empty. An empty `authorId` would be a filter matching nobody.
    expect(query.has('authorId')).toBe(false)
    expect(query.has('from')).toBe(false)
  })

  it('drops empty-string filters', async () => {
    const { calls, request } = stubRequest({
      status: 200,
      body: { items: [], nextCursor: null, truncated: false },
    })

    // What an unfilled date input actually produces. Forwarded verbatim it would be a filter the
    // server cannot satisfy, returning nothing and looking like "no results".
    await createMessagingClient(request).searchMessages('report', { from: '', authorId: '' })

    const query = new URLSearchParams(at(at(calls, 0).path.split('?'), 1))

    expect(query.has('from')).toBe(false)
    expect(query.has('authorId')).toBe(false)
  })

  it('encodes the query rather than interpolating it', async () => {
    const { calls, request } = stubRequest({
      status: 200,
      body: { items: [], nextCursor: null, truncated: false },
    })

    await createMessagingClient(request).searchMessages('a&b=c #tag')

    const query = new URLSearchParams(at(at(calls, 0).path.split('?'), 1))

    expect(query.get('q')).toBe('a&b=c #tag')
  })

  it('carries the truncation flag through', async () => {
    const { request } = stubRequest({
      status: 200,
      body: { items: [], nextCursor: 'more', truncated: true },
    })

    // FR-033. A client that drops this tells its user there are no more results, which is the one
    // wrong answer — a truncated search and an exhaustive one are otherwise indistinguishable.
    const page = await createMessagingClient(request).searchMessages('report')

    expect(page.truncated).toBe(true)
    expect(page.nextCursor).toBe('more')
  })

  it('reports a failed search', async () => {
    const { request } = stubRequest({ status: 503 })

    await expect(createMessagingClient(request).searchMessages('x')).rejects.toMatchObject({
      status: 503,
    })
  })

  it('searches the directory', async () => {
    const { calls, request } = stubRequest({ status: 200, body: [] })
    const client = createMessagingClient(request)

    await client.searchEmployees('an')
    await client.searchEmployees('a&b', 5)

    expect(at(calls, 0).path).toBe('/directory/employees?q=an&limit=20')
    expect(at(calls, 1).path).toBe('/directory/employees?q=a%26b&limit=5')
  })
})

describe('meetings', () => {
  it('starts a meeting in a conversation', async () => {
    const { calls, request } = stubRequest({ status: 201, body: { id: 'meeting-1' } })

    await createMessagingClient(request).startMeeting('c1')

    expect(at(calls, 0).path).toBe('/conversations/c1/meetings')
    expect(at(calls, 0).init?.method).toBe('POST')
  })

  it('reports unavailability separately from any other failure', async () => {
    const { request } = stubRequest({ status: 503 })

    // FR-044's platform ceiling and an unreachable media host both arrive as 503, and the caller
    // has to be able to tell that from a general outage — messaging is unaffected in both cases.
    await expect(createMessagingClient(request).startMeeting('c1')).rejects.toMatchObject({
      status: 503,
    })
  })

  it('mints a join token per join', async () => {
    const { calls, request } = stubRequest({
      status: 201,
      body: { token: 't', mediaServerUrl: 'wss://livekit.test', expiresAt: 'x' },
    })

    const credential = await createMessagingClient(request).joinMeeting('meeting-1')

    // This call IS the meeting access control (FR-041): the media server trusts the token
    // completely, so membership is verified here and nowhere else.
    expect(at(calls, 0).path).toBe('/meetings/meeting-1/token')
    expect(credential.expiresAt).toBe('x')
  })

  it('reports a full meeting distinctly from a refusal', async () => {
    const { request } = stubRequest({ status: 409 })

    // 409 is FR-042's 25-participant ceiling; 403 would be "not a member". The client shows very
    // different things for the two.
    await expect(createMessagingClient(request).joinMeeting('m1')).rejects.toMatchObject({
      status: 409,
    })
  })

  it('claims and releases the share slot', async () => {
    const { calls, request } = stubRequest(
      { status: 201, body: { displacedEmployeeId: 'e2' } },
      { status: 204 },
    )

    const client = createMessagingClient(request)

    const claim = await client.startShare('meeting-1', 'window')

    expect(at(calls, 0).path).toBe('/meetings/meeting-1/share')
    expect(bodyOf(calls, 0)).toEqual({ scope: 'window' })

    // The scope is recorded rather than inferred, because FR-048 makes a promise about the window
    // case specifically and an audit record that could not tell them apart could not evidence it.
    expect(claim.displacedEmployeeId).toBe('e2')

    await client.stopShare('meeting-1')

    expect(at(calls, 1).init?.method).toBe('DELETE')
  })

  it('reports a refused release rather than resolving', async () => {
    const { request } = stubRequest({ status: 403 })

    // Resolving silently would leave the control showing "stop sharing" for a slot the server
    // still believes is held, and nobody else could take it.
    await expect(createMessagingClient(request).stopShare('m1')).rejects.toMatchObject({
      status: 403,
    })
  })
})
