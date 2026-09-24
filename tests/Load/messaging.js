/**
 * T107 — k6 load test for the send and history budgets.
 *
 * plan.md's declared budget, and the two rows this script measures:
 *
 *   | POST /conversations/{id}/messages accept      | p95 150 ms | p99 300 ms |
 *   | GET  /conversations/{id}/messages (50 items)  | p95 250 ms | p99 500 ms |
 *
 * Thresholds are assertions, not decoration: k6 exits non-zero when one is breached, which is what
 * makes this usable as CI gate 6 rather than a report somebody reads occasionally.
 *
 * **Run it against a database seeded to full retention volume.** A budget verified on a thousand
 * rows is not verified — see tests/Load/README.md, which is the whole point of T108.
 *
 * Usage:
 *   k6 run -e BASE_URL=http://localhost:8081 -e KEYCLOAK_URL=http://localhost:8082 \
 *          -e REALM=internalchat tests/Load/messaging.js
 */

import http from 'k6/http';
import { check, fail } from 'k6';
import { Trend } from 'k6/metrics';
import { randomSeed } from 'k6';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8081';
const KEYCLOAK_URL = __ENV.KEYCLOAK_URL || 'http://localhost:8082';
const REALM = __ENV.REALM || 'internalchat';
const CLIENT_ID = __ENV.CLIENT_ID || 'internalchat-test';

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

/** Separated from the built-in http metrics so a breach names the operation, not just "a request". */
const sendAccept = new Trend('send_accept_ms', true);
const historyPage = new Trend('history_page_ms', true);

export const options = {
  scenarios: {
    /**
     * 100 messages/second sustained with a 1,000/second burst (plan.md, Scale/Scope).
     *
     * An arrival-rate scenario rather than a fixed VU count: the budget is stated per second of
     * offered load, and a VU-based test measures whatever throughput the system happens to allow
     * instead of holding the rate and reporting the latency.
     */
    steady_send: {
      executor: 'constant-arrival-rate',
      rate: 100,
      timeUnit: '1s',
      duration: '2m',
      preAllocatedVUs: 50,
      maxVUs: 400,
      exec: 'sendMessage',
    },
    burst_send: {
      executor: 'constant-arrival-rate',
      rate: 1000,
      timeUnit: '1s',
      duration: '20s',
      startTime: '2m30s',
      preAllocatedVUs: 300,
      maxVUs: 2000,
      exec: 'sendMessage',
    },
    read_history: {
      executor: 'constant-arrival-rate',
      rate: 50,
      timeUnit: '1s',
      duration: '2m',
      preAllocatedVUs: 30,
      maxVUs: 200,
      exec: 'readHistory',
    },
  },

  thresholds: {
    'send_accept_ms': ['p(95)<150', 'p(99)<300'],
    'history_page_ms': ['p(95)<250', 'p(99)<500'],

    // A fast p95 achieved by failing half the requests is not a pass. Kept tight rather than at
    // zero: the send endpoint is rate-limited by design (T043), and a burst legitimately produces
    // some 429s.
    'http_req_failed': ['rate<0.01'],
  },
};

/** Crockford base32, matching the server's `ClientMessageKey`. */
const ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/**
 * A ULID-shaped idempotency key.
 *
 * Unique per iteration, deliberately. This script measures the accept path, so every send must be a
 * new message — reusing a key would exercise the deduplication read instead and report a latency
 * that has nothing to do with the budget. Idempotent *retry* behaviour is asserted in
 * tests/Integration/Messages/IdempotentSendTests.cs, where correctness can actually be checked.
 */
function newClientMessageKey() {
  let key = '';
  for (let i = 0; i < 26; i++) {
    key += ALPHABET[Math.floor(Math.random() * ALPHABET.length)];
  }
  return key;
}

/** Obtains an access token via the realm's direct access grant. */
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
 * One-time setup: a token and a conversation id per user.
 *
 * Done here rather than per iteration because a token request inside the measured loop would add
 * Keycloak's latency to every sample and put the identity provider under load this test is not
 * trying to measure.
 */
export function setup() {
  randomSeed(20260811);

  const sessions = USERS.map((username) => {
    const token = tokenFor(username);

    const conversations = http.get(`${BASE_URL}/api/v1/conversations?limit=5`, {
      headers: { Authorization: `Bearer ${token}` },
      tags: { name: 'conversations' },
    });

    if (conversations.status !== 200) {
      fail(`Could not list conversations for ${username} (${conversations.status}).`);
    }

    const items = conversations.json('items');

    if (!items || items.length === 0) {
      fail(
        `${username} has no conversations. Run the seeder first: ` +
          'dotnet run --project tools/Seeder -- --dev',
      );
    }

    return { username, token, conversationId: items[0].id };
  });

  return { sessions };
}

/** Sends one message and records the accept latency. */
export function sendMessage(data) {
  const session = data.sessions[Math.floor(Math.random() * data.sessions.length)];

  const response = http.post(
    `${BASE_URL}/api/v1/conversations/${session.conversationId}/messages`,
    JSON.stringify({
      clientMessageKey: newClientMessageKey(),
      body: `load probe ${Date.now()}`,
    }),
    {
      headers: {
        Authorization: `Bearer ${session.token}`,
        'Content-Type': 'application/json',
      },
      tags: { name: 'send' },
    },
  );

  // 429 is a correct answer under burst, not a failure — the rate limiter is doing its job (T043).
  // Recording its latency would measure how fast the platform says no.
  if (response.status !== 429) {
    sendAccept.add(response.timings.duration);
  }

  check(response, {
    'send accepted or rate limited': (r) => r.status === 201 || r.status === 200 || r.status === 429,
  });
}

/** Reads one page of history and records the latency. */
export function readHistory(data) {
  const session = data.sessions[Math.floor(Math.random() * data.sessions.length)];

  const response = http.get(
    `${BASE_URL}/api/v1/conversations/${session.conversationId}/messages?limit=50`,
    {
      headers: { Authorization: `Bearer ${session.token}` },
      tags: { name: 'history' },
    },
  );

  if (response.status !== 429) {
    historyPage.add(response.timings.duration);
  }

  check(response, {
    'history returned': (r) => r.status === 200 || r.status === 429,
  });
}
