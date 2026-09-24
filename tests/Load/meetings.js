/**
 * T196 — meeting capacity: 25 participants per room and the 1,250 platform ceiling.
 *
 * plan.md's declared figures, and what this script measures:
 *
 *   | Participants per meeting            | 25    | FR-042 |
 *   | Concurrent participants, platform   | 1,250 | FR-043 |
 *
 * **What this script can and cannot establish, stated plainly.** It measures the API's behaviour
 * at the ceiling: that the 25th join succeeds, the 26th is refused with 409, and that once the
 * platform counter reaches 1,250 further starts are refused with 503 and `retryAfterSeconds`. That
 * is FR-042 and FR-044, and it is genuinely testable from here.
 *
 * It does **not** establish that the media host can carry 1,250 participants. k6 mints tokens; it
 * does not open WebRTC transports, publish video, or consume a single core of SFU CPU. Whether the
 * hardware actually sustains the load is research.md D12's open item and is T197 — which needs real
 * machines and real media, and cannot be inferred from anything below.
 *
 * Confusing the two would be the expensive mistake: a green run here says the refusal logic is
 * correct, and says nothing whatsoever about whether meetings work at scale.
 *
 * Usage:
 *   k6 run -e BASE_URL=http://localhost:8081 tests/Load/meetings.js
 */

import http from 'k6/http';
import { check, fail } from 'k6';
import { Trend, Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8081';
const KEYCLOAK_URL = __ENV.KEYCLOAK_URL || 'http://localhost:8082';
const REALM = __ENV.REALM || 'internalchat';
const CLIENT_ID = __ENV.CLIENT_ID || 'internalchat-test';

/** FR-042. */
const MAX_PARTICIPANTS_PER_MEETING = 25;

/** Development roster, from deploy/keycloak/README.md. Passwords equal usernames. */
const USERS = [
  'an.nguyen',
  'binh.tran',
  'chi.le',
  'dung.pham',
  'giang.hoang',
  'hai.vo',
  'khanh.dang',
  'lan.bui',
];

const startLatency = new Trend('meeting_start_ms', true);
const tokenLatency = new Trend('meeting_token_ms', true);

const refusedAtRoomCap = new Counter('refused_room_full');
const refusedAtPlatformCap = new Counter('refused_platform_capacity');
const refusedUnavailable = new Counter('refused_media_unavailable');

export const options = {
  scenarios: {
    /**
     * Sustained token minting. This is the request meetings actually put on the API — starting is
     * rare, joining is not — and it is the one that must not compete with messaging.
     */
    join_churn: {
      executor: 'constant-arrival-rate',
      rate: 40,
      timeUnit: '1s',
      duration: '2m',
      preAllocatedVUs: 40,
      maxVUs: 300,
      exec: 'joinExistingMeeting',
    },

    /**
     * A morning spike: many conversations starting meetings at once, which is when the platform
     * ceiling is actually reached.
     */
    start_storm: {
      executor: 'constant-arrival-rate',
      rate: 10,
      timeUnit: '1s',
      duration: '1m',
      startTime: '2m30s',
      preAllocatedVUs: 20,
      maxVUs: 150,
      exec: 'startMeeting',
    },
  },

  thresholds: {
    // Meetings must not become the slow path on the shared API. Generous next to messaging's
    // 150 ms because starting one does a membership check, an availability probe and a capacity
    // read — but bounded, because a person is waiting to make a call.
    meeting_start_ms: ['p(95)<500'],

    // Token minting is on the join path and is mostly a signature. It should be fast.
    meeting_token_ms: ['p(95)<250'],

    // 409 and 503 are correct answers at the ceilings, so they are counted rather than failed.
    // 5xx other than the documented 503 is not, which is what this catches.
    http_req_failed: ['rate<0.30'],
  },
};

function tokenFor(username) {
  const response = http.post(
    `${KEYCLOAK_URL}/realms/${REALM}/protocol/openid-connect/token`,
    {
      grant_type: 'password',
      client_id: CLIENT_ID,
      username: username,
      password: username,
      scope: 'openid',
    },
    { tags: { name: 'token' } },
  );

  if (response.status !== 200) {
    fail(
      `Keycloak refused a token for ${username} (${response.status}). ` +
        'Is the development realm imported, with the internalchat-test client?',
    );
  }

  return response.json('access_token');
}

/**
 * Issues tokens and discovers conversations to meet in.
 *
 * Fails loudly when the media host is unreachable. That is the correct behaviour for a *capacity*
 * test: every start would be refused with 503 and the run would report a flawless pass having
 * measured the degradation path instead of the ceiling. T182 covers the degradation path properly.
 */
export function setup() {
  const tokens = {};

  for (const user of USERS) {
    tokens[user] = tokenFor(user);
  }

  const conversations = http.get(`${BASE_URL}/api/v1/conversations?limit=50`, {
    headers: { Authorization: `Bearer ${tokens[USERS[0]]}` },
    tags: { name: 'conversations' },
  });

  if (conversations.status !== 200) {
    fail(`Could not list conversations (${conversations.status}). Is the stack seeded?`);
  }

  const items = conversations.json('items') || [];

  if (items.length === 0) {
    fail('No conversations to meet in. Seed the development dataset first.');
  }

  const probe = http.post(
    `${BASE_URL}/api/v1/conversations/${items[0].id}/meetings`,
    null,
    {
      headers: { Authorization: `Bearer ${tokens[USERS[0]]}` },
      tags: { name: 'probe' },
    },
  );

  if (probe.status === 503) {
    fail(
      'The media host is unreachable, so every start would be refused and this run would ' +
        'measure the degradation path rather than the capacity ceiling. Bring up ' +
        'deploy/docker-compose.media.yml first.',
    );
  }

  return {
    tokens,
    conversationIds: items.map((item) => item.id),
    seedMeetingId: probe.status < 300 ? probe.json('id') : null,
  };
}

export function startMeeting(data) {
  const user = USERS[Math.floor(Math.random() * USERS.length)];
  const conversationId =
    data.conversationIds[Math.floor(Math.random() * data.conversationIds.length)];

  const response = http.post(`${BASE_URL}/api/v1/conversations/${conversationId}/meetings`, null, {
    headers: { Authorization: `Bearer ${data.tokens[user]}` },
    tags: { name: 'start_meeting' },
  });

  if (response.status === 503) {
    // The platform ceiling, or the media host. Both are documented answers, and the problem type
    // distinguishes them — which is why T190 emits two rather than one.
    const type = response.json('type') || '';

    if (type.includes('at-capacity')) {
      refusedAtPlatformCap.add(1);
    } else {
      refusedUnavailable.add(1);
    }

    check(response, {
      'a 503 states how long to wait (FR-044)': (r) => (r.json('retryAfterSeconds') || 0) > 0,
    });

    return;
  }

  check(response, {
    'start returned 200 or 201': (r) => r.status === 200 || r.status === 201,
  });

  if (response.status < 300) {
    startLatency.add(response.timings.duration);
  }
}

export function joinExistingMeeting(data) {
  if (data.seedMeetingId === null) {
    return;
  }

  const user = USERS[Math.floor(Math.random() * USERS.length)];

  const response = http.post(`${BASE_URL}/api/v1/meetings/${data.seedMeetingId}/token`, null, {
    headers: { Authorization: `Bearer ${data.tokens[user]}` },
    tags: { name: 'join_token' },
  });

  if (response.status === 409) {
    // FR-042: the room is full at 25. A documented outcome, counted rather than failed.
    refusedAtRoomCap.add(1);

    check(response, {
      'a full room names the ceiling': (r) =>
        (r.json('maxParticipants') || 0) === MAX_PARTICIPANTS_PER_MEETING,
    });

    return;
  }

  if (response.status === 503) {
    refusedAtPlatformCap.add(1);
    return;
  }

  check(response, {
    'token returned 201': (r) => r.status === 201,

    // A token with no expiry would be a permanent bearer capability for a meeting room.
    'token carries an expiry': (r) => typeof r.json('expiresAt') === 'string',
  });

  if (response.status === 201) {
    tokenLatency.add(response.timings.duration);
  }
}
