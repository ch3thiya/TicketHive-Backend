// Availability polling under load: GET /api/inventory/shows/{showId}/availability
//
// Every customer on an event page polls availability every 3 seconds
// (EventDetail.tsx, ADR-010), signed in or not. During an on-sale this is the
// highest-volume read in the system, and it always reads the database
// (Cache-Control: no-store), so it competes with hold writes for the same rows.
//
// Target: p95 < 300 ms. ADR-014 sets no figure for this endpoint; 300 ms
// matches its target for the other polled endpoint (queue status).
import http from 'k6/http';
import { check } from 'k6';
import { baseUrl, seed, SUMMARY_TREND_STATS } from '../lib/config.js';
import { scenario, think } from '../lib/profiles.js';
import { buildSummary } from '../lib/summary.js';
import { warmUp, WARMUP_TAGS } from '../lib/warmup.js';

const AVAILABILITY_URL = `${baseUrl('inventory')}/api/inventory/shows/${seed.capacity.showId}/availability`;

export const options = {
  scenarios: { poll_availability: scenario('pollAvailability') },
  summaryTrendStats: SUMMARY_TREND_STATS,
  thresholds: {
    'http_req_duration{name:availability}': ['p(95)<300'],
    'http_req_failed{name:availability}': ['rate<0.01'],
    checks: ['rate>0.99'],
  },
};

export function setup() {
  warmUp(() => http.get(AVAILABILITY_URL, { tags: WARMUP_TAGS }));
}

export function pollAvailability() {
  // Anonymous on purpose: the endpoint is public, as it is for real visitors.
  const res = http.get(AVAILABILITY_URL, { tags: { name: 'availability' } });

  check(res, {
    'status 200': (r) => r.status === 200,
    'lists the seeded category': (r) =>
      r.status === 200 && r.json('categories.0.categoryId') === seed.capacity.categoryId,
    'available never negative': (r) => r.status === 200 && r.json('categories.0.available') >= 0,
    'not cached (no-store)': (r) => (r.headers['Cache-Control'] || '').includes('no-store'),
  });

  think(3);
}

export function handleSummary(data) {
  return buildSummary(data, { testName: 'availability' });
}
