// Browsing under load
//   GET /api/catalog/events            event list (home page)
//   GET /api/catalog/events/{eventId}  event detail page
//
// What visitors do before and during an on-sale. Both endpoints are public.
// The event list returns every published event in one response (no paging),
// so its cost grows with the catalogue: seed.sh adds BROWSE_EVENTS events
// (default 50) to make that visible.
//
// Target: p95 < 500 ms (team-defined; ADR-014 sets none for Catalog).
import http from 'k6/http';
import { check, group } from 'k6';
import { baseUrl, seed, SUMMARY_TREND_STATS } from '../lib/config.js';
import { scenario, think } from '../lib/profiles.js';
import { buildSummary } from '../lib/summary.js';

const EVENTS_URL = `${baseUrl('catalog')}/api/catalog/events`;

export const options = {
  scenarios: { browse: scenario('browse') },
  summaryTrendStats: SUMMARY_TREND_STATS,
  thresholds: {
    'http_req_duration{name:event_list}': ['p(95)<500'],
    'http_req_failed{name:event_list}': ['rate<0.01'],
    'http_req_duration{name:event_detail}': ['p(95)<500'],
    'http_req_failed{name:event_detail}': ['rate<0.01'],
    checks: ['rate>0.99'],
  },
};

export function browse() {
  group('home page', () => {
    const list = http.get(EVENTS_URL, { tags: { name: 'event_list' } });
    check(list, {
      'event list 200': (r) => r.status === 200,
      'event list is not empty': (r) => r.status === 200 && r.json('#') > 0,
    });
  });

  think(3);

  group('event page', () => {
    const detail = http.get(`${EVENTS_URL}/${seed.eventId}`, { tags: { name: 'event_detail' } });
    check(detail, {
      'event detail 200': (r) => r.status === 200,
      'is the seeded event': (r) => r.status === 200 && r.json('id') === seed.eventId,
    });
  });

  think(5);
}

export function handleSummary(data) {
  return buildSummary(data, { testName: 'browse' });
}
