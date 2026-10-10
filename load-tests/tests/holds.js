// Hold creation under load: POST /api/inventory/holds
//
// The busiest write in TicketHive: every customer who picks tickets creates
// a hold, and they all do it at the moment sales open.
//
// Target (ADR-014): hold p95 < 500 ms with 1,000 virtual users.
//
// The show is seeded with effectively unlimited stock and a very high
// per-customer limit, so every request should succeed: the test measures the
// hold path itself (auth, rate limiter, quota row, conditional stock update,
// hold insert, all in one transaction), not sell-out behaviour. Sell-out and
// correctness under contention are tests/oversell.js.
import http from 'k6/http';
import { check } from 'k6';
import { baseUrl, seed, SUMMARY_TREND_STATS } from '../lib/config.js';
import { scenario, think } from '../lib/profiles.js';
import { authHeaders } from '../lib/auth.js';
import { buildSummary } from '../lib/summary.js';

const HOLDS_URL = `${baseUrl('inventory')}/api/inventory/holds`;

export const options = {
  scenarios: { create_holds: scenario('createHold') },
  summaryTrendStats: SUMMARY_TREND_STATS,
  thresholds: {
    'http_req_duration{name:create_hold}': ['p(95)<500'], // ADR-014 target
    'http_req_failed{name:create_hold}': ['rate<0.01'],
    checks: ['rate>0.99'],
  },
};

export function createHold() {
  const payload = JSON.stringify({
    showId: seed.capacity.showId,
    items: [{ categoryId: seed.capacity.categoryId, quantity: 1 }],
  });

  const res = http.post(HOLDS_URL, payload, {
    headers: authHeaders({
      'Content-Type': 'application/json',
      // A fresh key per attempt: each request is a new hold, not a retry.
      'Idempotency-Key': `load-${__VU}-${__ITER}-${Date.now()}`,
    }),
    tags: { name: 'create_hold' },
    // Only 201 counts as success here; anything else is a failure.
    responseCallback: http.expectedStatuses(201),
  });

  check(res, {
    'hold created (201)': (r) => r.status === 201,
    'hold is Active': (r) => r.status === 201 && r.json('status') === 'Active',
    'hold has the requested quantity': (r) => r.status === 201 && r.json('items.0.quantity') === 1,
  });

  think();
}

export function handleSummary(data) {
  return buildSummary(data, { testName: 'holds' });
}
