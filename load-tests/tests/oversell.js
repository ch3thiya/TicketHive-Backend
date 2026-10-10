// Oversell and hold release, end to end: POST /api/inventory/holds
//
// OVERSELL_USERS customers (default 500) each try to hold one ticket for a
// show with only `seed.contention.stock` tickets (default 100), all at once.
//
// HoldsConcurrencyTests already proves the same invariant inside one process
// with a test database. This test proves it for the running system: real
// HTTP through the gateway, real token validation, the rate limiter, and the
// production connection pool, under a burst of simultaneous requests.
//
// Invariants checked after the burst (lib/summary.js -> verdict file):
//   1. no oversell: successful holds never exceed stock
//   2. stock is conserved: successful holds + tickets still available = stock
//   3. every refused request is a clean 409 "no longer available", never a 5xx
//   4. (RELEASE_CHECK=1) expired holds are released within 30 s of expiry
//      (ADR-014). The contention show is seeded with a 1-minute hold time.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Gauge, Trend } from 'k6/metrics';
import { baseUrl, seed, SUMMARY_TREND_STATS } from '../lib/config.js';
import { authHeaders, warmupHeaders } from '../lib/auth.js';
import { warmUp, WARMUP_TAGS } from '../lib/warmup.js';
import { buildSummary } from '../lib/summary.js';

const USERS = Number(__ENV.OVERSELL_USERS || 500);
const RELEASE_CHECK = (__ENV.RELEASE_CHECK || '1') === '1';
const RELEASE_TARGET_SECONDS = 30; // ADR-014
const HOLDS_URL = `${baseUrl('inventory')}/api/inventory/holds`;
const AVAILABILITY_URL = `${baseUrl('inventory')}/api/inventory/shows/${seed.contention.showId}/availability`;

const ticketsHeld = new Counter('tickets_held');
const soldOutRefusals = new Counter('sold_out_refusals');
const unexpectedResponses = new Counter('unexpected_responses');
// One counter per status code, because k6 only exposes metrics created in the
// init context. Status 0 is a transport failure (timeout, reset, refused).
const UNEXPECTED_STATUSES = [0, 400, 401, 403, 404, 408, 422, 429, 500, 502, 503, 504];
const unexpectedByStatus = Object.fromEntries(
  UNEXPECTED_STATUSES.map((status) => [status, new Counter(`unexpected_status_${status}`)]),
);
const unexpectedOther = new Counter('unexpected_status_other');
const holdExpiresAt = new Trend('hold_expires_at_epoch_ms');
const availableAfterBurst = new Gauge('available_after_burst');
const availableAfterRelease = new Gauge('available_after_release');
const releasedAt = new Gauge('released_at_epoch_ms');

export const options = {
  scenarios: {
    burst: {
      // Every VU fires exactly one request, all released together.
      executor: 'per-vu-iterations',
      vus: USERS,
      iterations: 1,
      maxDuration: '1m',
      exec: 'tryToHold',
    },
  },
  teardownTimeout: '4m', // the release check waits for holds to expire
  summaryTrendStats: SUMMARY_TREND_STATS,
  thresholds: {
    'http_req_duration{name:contended_hold}': ['p(95)<2000'],
    'http_req_failed{name:contended_hold}': ['rate<0.01'],
    unexpected_responses: ['count==0'],
  },
};

// Warm up on the capacity show: same code path, and no contention ticket is used.
export function setup() {
  warmUp((i) =>
    http.post(
      HOLDS_URL,
      JSON.stringify({ showId: seed.capacity.showId, items: [{ categoryId: seed.capacity.categoryId, quantity: 1 }] }),
      {
        headers: warmupHeaders(i, { 'Content-Type': 'application/json', 'Idempotency-Key': `warmup-${i}-${Date.now()}` }),
        tags: WARMUP_TAGS,
      },
    ),
  );
}

export function tryToHold() {
  const res = http.post(
    HOLDS_URL,
    JSON.stringify({
      showId: seed.contention.showId,
      items: [{ categoryId: seed.contention.categoryId, quantity: 1 }],
    }),
    {
      headers: authHeaders({ 'Content-Type': 'application/json', 'Idempotency-Key': `oversell-${__VU}` }),
      tags: { name: 'contended_hold' },
      // 409 is the correct answer once stock runs out, so it is not a failure.
      responseCallback: http.expectedStatuses(201, 409),
    },
  );

  if (res.status === 201) {
    ticketsHeld.add(1);
    holdExpiresAt.add(Date.parse(res.json('expiresAt')));
  } else if (res.status === 409) {
    soldOutRefusals.add(1);
  } else {
    unexpectedResponses.add(1);
    (unexpectedByStatus[res.status] || unexpectedOther).add(1);
  }

  check(res, { 'either held (201) or sold out (409)': (r) => r.status === 201 || r.status === 409 });
}

function available() {
  const res = http.get(AVAILABILITY_URL, { tags: { name: 'verify_availability' } });
  return res.status === 200 ? res.json('categories.0.available') : null;
}

// Runs once, after every VU has finished.
export function teardown() {
  availableAfterBurst.add(available());

  if (!RELEASE_CHECK) return;

  // Holds expire one minute after creation; the sweeper runs every 10 s.
  // Poll until every ticket is back in stock (or give up after 3 minutes).
  const deadline = Date.now() + 3 * 60 * 1000;
  while (Date.now() < deadline) {
    const now = available();
    if (now === seed.contention.stock) {
      releasedAt.add(Date.now());
      availableAfterRelease.add(now);
      return;
    }
    sleep(1);
  }
  availableAfterRelease.add(available());
}

function metricValue(data, name, stat) {
  return data.metrics[name]?.values[stat];
}

// "74 x 500, 3 x 0 (transport error)" or "none"
function unexpectedBreakdown(data) {
  const parts = [...UNEXPECTED_STATUSES, 'other']
    .map((status) => [status, metricValue(data, `unexpected_status_${status}`, 'count') || 0])
    .filter(([, count]) => count > 0)
    .map(([status, count]) => `${count} x ${status === 0 ? '0 (transport error)' : status}`);
  return parts.length ? parts.join(', ') : 'none';
}

export function handleSummary(data) {
  const stock = seed.contention.stock;
  const held = metricValue(data, 'tickets_held', 'count') || 0;
  const refused = metricValue(data, 'sold_out_refusals', 'count') || 0;
  const unexpected = metricValue(data, 'unexpected_responses', 'count') || 0;
  const afterBurst = metricValue(data, 'available_after_burst', 'value');

  const invariants = [
    {
      name: 'No oversell',
      ok: held <= stock,
      detail: `${held} tickets held for ${stock} in stock (${USERS} customers tried)`,
    },
    {
      name: 'Stock conserved',
      ok: afterBurst !== undefined && afterBurst >= 0 && held + afterBurst === stock,
      detail: `${held} held + ${afterBurst ?? '?'} still available = ${held + (afterBurst ?? 0)} (expected ${stock})`,
    },
    {
      name: 'Clean refusals',
      ok: unexpected === 0 && held + refused === USERS,
      detail: `${refused} clean 409s, ${unexpected} unexpected (${unexpectedBreakdown(data)})`,
    },
  ];

  if (RELEASE_CHECK) {
    const lastExpiry = metricValue(data, 'hold_expires_at_epoch_ms', 'max');
    const restored = metricValue(data, 'released_at_epoch_ms', 'value');
    const delaySeconds = lastExpiry && restored ? (restored - lastExpiry) / 1000 : undefined;
    invariants.push({
      name: 'Expired holds released (ADR-014)',
      ok: delaySeconds !== undefined && delaySeconds <= RELEASE_TARGET_SECONDS,
      detail:
        delaySeconds === undefined
          ? `stock not fully restored within 3 min (available: ${metricValue(data, 'available_after_release', 'value') ?? '?'})`
          : `all ${held} holds released ${Math.max(delaySeconds, 0).toFixed(1)} s after the last one expired (target <= ${RELEASE_TARGET_SECONDS} s)`,
    });
  }

  return buildSummary(data, { testName: 'oversell', invariants });
}
