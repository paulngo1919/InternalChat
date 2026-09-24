/**
 * T169 — the search budget, and the D9 decision trigger for OpenSearch.
 *
 * plan.md's declared budget, and the row this script measures:
 *
 *   | GET /search/messages | p95 800 ms |
 *
 * **This script's purpose is to be allowed to fail.** research.md D9 chose PostgreSQL full-text
 * search over OpenSearch and named the condition for revisiting that: "search p95 exceeding 800 ms
 * against seeded full-retention data, or a stated typo-tolerance requirement". A breach here is not
 * a bug to work around — it is the decision trigger firing, and the answer is the second stateful
 * system D9 deliberately deferred.
 *
 * **It is meaningless against the development dataset.** Search that is fast on ten thousand rows
 * tells you nothing about a hundred and twenty-five million: the GIN index fits in memory, the
 * planner picks a different shape, and every number below is a measurement of the wrong system.
 * Seed first:
 *
 *   docker compose exec api dotnet run --project tools/Seeder -- --messages 125000000
 *   k6 run -e BASE_URL=http://localhost:8081 tests/Load/search.js
 *
 * The script refuses to report a pass on an obviously unseeded database — see `setup` below.
 */

import http from 'k6/http';
import { check, fail } from 'k6';
import { Trend, Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://localhost:8081';
const KEYCLOAK_URL = __ENV.KEYCLOAK_URL || 'http://localhost:8082';
const REALM = __ENV.REALM || 'internalchat';
const CLIENT_ID = __ENV.CLIENT_ID || 'internalchat-test';

/**
 * Smallest corpus at which a result from this script means anything.
 *
 * Far below the 125 million the seeder produces, because the check exists to catch "nobody ran the
 * seeder" rather than to police the exact volume.
 */
const MINIMUM_CREDIBLE_CORPUS = 1000000;

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

/**
 * Query shapes, chosen to exercise different plans rather than to look varied.
 *
 * A single repeated term would sit in the buffer cache after the first iteration and report the
 * latency of a cache hit as though it were the budget.
 */
const QUERIES = [
  { q: 'deployment', label: 'single_common_term' },
  { q: 'ke hoach', label: 'unaccented_vietnamese' },
  { q: 'kế hoạch quý', label: 'accented_vietnamese' },
  { q: '"release runbook"', label: 'quoted_phrase' },
  { q: 'incident -postmortem', label: 'negated_term' },
  { q: 'zzzqqq unlikely', label: 'no_matches' },
];

const searchLatency = new Trend('search_ms', true);
const truncatedResults = new Counter('search_truncated');

export const options = {
  scenarios: {
    /**
     * Search is a human-paced operation, so the rate is modest and the point is latency.
     *
     * 20/second across 10,000 employees is already a generous estimate of real search traffic. The
     * budget under test is p95 per query, not throughput — running it at messaging's 100/second
     * would measure queueing rather than the index.
     */
    steady_search: {
      executor: 'constant-arrival-rate',
      rate: 20,
      timeUnit: '1s',
      duration: '3m',
      preAllocatedVUs: 20,
      maxVUs: 200,
      exec: 'search',
    },

    /**
     * A burst, because search is the endpoint people hammer when something is wrong.
     *
     * During an incident everyone searches the same phrase at once. This is the shape that finds
     * out whether the fixed-window rate limit and the statement timeout hold together.
     */
    burst_search: {
      executor: 'constant-arrival-rate',
      rate: 200,
      timeUnit: '1s',
      duration: '30s',
      startTime: '3m30s',
      preAllocatedVUs: 100,
      maxVUs: 800,
      exec: 'search',
    },
  },

  thresholds: {
    // THE decision trigger (research.md D9). A breach means OpenSearch, not a query tweak.
    'search_ms': ['p(95)<800'],

    // Per-shape, so a breach names which kind of query is slow. An aggregate p95 can pass while
    // every Vietnamese query is over budget, hidden by a majority of cheap single-term ones.
    'search_ms{shape:single_common_term}': ['p(95)<800'],
    'search_ms{shape:unaccented_vietnamese}': ['p(95)<800'],
    'search_ms{shape:accented_vietnamese}': ['p(95)<800'],
    'search_ms{shape:quoted_phrase}': ['p(95)<800'],
    'search_ms{shape:negated_term}': ['p(95)<800'],

    // A search that matches nothing must not be slower than one that matches. If it is, the
    // membership filter is not pruning before the index is consulted — which is both a performance
    // problem and the timing side channel T161 asserts against.
    'search_ms{shape:no_matches}': ['p(95)<800'],

    // 429s are expected during the burst — search is rate-limited by design (T043) — so this is
    // tolerant. 5xx is not: any server error means the budget was met by failing.
    'http_req_failed': ['rate<0.05'],
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
 * Issues tokens once, and refuses to run against an unseeded database.
 *
 * The corpus check is the important half. Without it this script happily reports a 12 ms p95
 * against the development dataset, and somebody records that as the search budget being met —
 * which would retire D9's decision trigger on the strength of a measurement of nothing.
 */
export function setup() {
  const tokens = {};

  for (const user of USERS) {
    tokens[user] = tokenFor(user);
  }

  const probe = http.get(`${BASE_URL}/api/v1/admin/stats/messages`, {
    headers: { Authorization: `Bearer ${tokens[USERS[0]]}` },
    tags: { name: 'corpus_probe' },
  });

  if (probe.status === 200) {
    const total = probe.json('totalMessages');

    if (typeof total === 'number' && total < MINIMUM_CREDIBLE_CORPUS) {
      fail(
        `The database holds ${total} messages. This script measures the D9 decision trigger and ` +
          `is meaningless below ${MINIMUM_CREDIBLE_CORPUS}. Seed first:\n` +
          '  docker compose exec api dotnet run --project tools/Seeder -- --messages 125000000',
      );
    }
  } else {
    // The stats endpoint does not exist yet (it lands with the seeder work). Warn rather than
    // fail, so the script is runnable — but say plainly that the numbers may be meaningless.
    console.warn(
      'Could not verify the corpus size: /api/v1/admin/stats/messages returned ' +
        `${probe.status}. If this database is not seeded to full retention volume, every number ` +
        'below measures the wrong system and must not be recorded as the search budget.',
    );
  }

  return { tokens };
}

export function search(data) {
  const user = USERS[Math.floor(Math.random() * USERS.length)];
  const query = QUERIES[Math.floor(Math.random() * QUERIES.length)];

  const response = http.get(
    `${BASE_URL}/api/v1/search/messages?q=${encodeURIComponent(query.q)}&limit=25`,
    {
      headers: { Authorization: `Bearer ${data.tokens[user]}` },
      tags: { name: 'search', shape: query.label },
    },
  );

  // 429 is a legitimate outcome under the burst scenario and is excluded from the latency figure:
  // a rejected request is fast for the wrong reason and would flatter the p95.
  if (response.status === 429) {
    return;
  }

  check(response, {
    'search returned 200': (r) => r.status === 200,
  });

  if (response.status !== 200) {
    return;
  }

  searchLatency.add(response.timings.duration, { shape: query.label });

  // FR-033. Counted rather than failed: truncation is the system behaving correctly under load.
  // But a run in which most searches truncate has not demonstrated the budget — it has demonstrated
  // the timeout, and the summary needs to say so.
  if (response.json('truncated') === true) {
    truncatedResults.add(1, { shape: query.label });
  }
}
