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
 * Since 002 it also measures what the employee waits for — **delivery**, not just acceptance:
 *
 *   | Sent message → MessageReceived on a listening hub connection | p95 300 ms | p99 500 ms |  (002 SC-001, SC-007)
 *
 * One listener per development user holds a real SignalR connection (JSON protocol over a raw
 * WebSocket) for the whole run and records `now − message.sentAt` for every load probe it receives.
 * `sentAt` is the server's clock, so run k6 on the application host (or an NTP-synced one); skew
 * shows up directly as lag.
 *
 * Usage:
 *   k6 run -e BASE_URL=http://localhost:8081 -e KEYCLOAK_URL=http://localhost:8082 \
 *          -e REALM=internalchat tests/Load/messaging.js
 *
 *   Optional: -e GROUP_CONVERSATION_ID=<id> sends the burst into that conversation instead — seed a
 *   500-member group first (tests/Load/README.md) to measure 002 SC-003.
 */

import http from 'k6/http';
import { check, fail } from 'k6';
import exec from 'k6/execution';
import { Trend } from 'k6/metrics';
import { randomSeed } from 'k6';
import { WebSocket } from 'k6/experimental/websockets';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8081';
const KEYCLOAK_URL = __ENV.KEYCLOAK_URL || 'http://localhost:8082';
const REALM = __ENV.REALM || 'internalchat';
const CLIENT_ID = __ENV.CLIENT_ID || 'internalchat-test';
const GROUP_CONVERSATION_ID = __ENV.GROUP_CONVERSATION_ID || '';

/** SignalR's record separator: every JSON-protocol frame ends with it. */
const RS = '\x1e';

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

/** 002 — send to MessageReceived on another connection. Tagged by scope, so the group burst has its own threshold. */
const deliveryLag = new Trend('delivery_lag_ms', true);

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
    /**
     * 002 — one hub listener per development user for the length of the run. Constant VUs, not an
     * arrival rate: a listener is a long-lived connection, and its measurements are the point.
     */
    listen_delivery: {
      executor: 'per-vu-iterations',
      vus: USERS.length,
      iterations: 1,
      maxDuration: '3m30s',
      exec: 'listenForDelivery',
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

    // 002 SC-001 / SC-007, at 100 msg/s sustained. And SC-003 for the burst when it targets a
    // 500-member group — scoped by tag so a slow burst cannot hide inside the steady numbers.
    'delivery_lag_ms{scope:steady}': ['p(95)<300', 'p(99)<500'],
    'delivery_lag_ms{scope:burst}': [GROUP_CONVERSATION_ID ? 'p(95)<1000' : 'p(95)<300'],

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

  // The burst goes to the large group when one is given (002 SC-003); everything else to the
  // sender's own first conversation.
  const inBurst = exec.scenario.name === 'burst_send';
  const conversationId = inBurst && GROUP_CONVERSATION_ID ? GROUP_CONVERSATION_ID : session.conversationId;

  const response = http.post(
    `${BASE_URL}/api/v1/conversations/${conversationId}/messages`,
    JSON.stringify({
      clientMessageKey: newClientMessageKey(),
      body: `load probe ${inBurst ? 'burst' : 'steady'} ${Date.now()}`,
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

/**
 * 002 — holds one SignalR connection for the run and records delivery lag for every load probe.
 *
 * Speaks the JSON hub protocol directly: negotiate, open the socket with the connection token,
 * handshake, then read `MessageReceived` invocations. Pings every 10 s, because the server drops a
 * connection it has not heard from in 30.
 */
export function listenForDelivery(data) {
  const session = data.sessions[(exec.vu.idInTest - 1) % data.sessions.length];

  const negotiated = http.post(`${BASE_URL}/hubs/chat/negotiate?negotiateVersion=1`, null, {
    headers: { Authorization: `Bearer ${session.token}` },
    tags: { name: 'negotiate' },
  });

  if (negotiated.status !== 200) {
    fail(`Hub negotiate failed for ${session.username} (${negotiated.status}).`);
  }

  const connectionToken = negotiated.json('connectionToken');
  const wsBase = BASE_URL.replace(/^http/, 'ws');
  const socket = new WebSocket(
    `${wsBase}/hubs/chat?id=${encodeURIComponent(connectionToken)}&access_token=${encodeURIComponent(session.token)}`,
  );

  let pinger = null;

  socket.addEventListener('open', () => {
    socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RS);
    pinger = setInterval(() => socket.send(JSON.stringify({ type: 6 }) + RS), 10000);
  });

  socket.addEventListener('message', (event) => {
    const received = Date.now();

    for (const frame of String(event.data).split(RS)) {
      if (!frame) {
        continue;
      }

      const message = JSON.parse(frame);

      if (message.type !== 1 || message.target !== 'MessageReceived') {
        continue;
      }

      const payload = message.arguments[0];

      // Only this script's probes, and only other people's: a sender's own echo on its other
      // connection is FR-005's concern and measured in Playwright.
      if (!payload || typeof payload.body !== 'string' || !payload.body.startsWith('load probe ')) {
        continue;
      }

      const scope = payload.body.startsWith('load probe burst') ? 'burst' : 'steady';
      deliveryLag.add(received - Date.parse(payload.sentAt), { scope });
    }
  });

  // Listen through the steady and burst scenarios, then leave cleanly.
  setTimeout(() => {
    if (pinger) {
      clearInterval(pinger);
    }

    socket.close();
  }, 3 * 60 * 1000);
}
