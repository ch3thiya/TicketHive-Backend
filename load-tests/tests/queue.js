// Waiting room under load
//   POST /api/waiting-room/queues/{showId}/entries      join the queue (once per customer)
//   GET  /api/waiting-room/queues/{showId}/entries/me   poll position every 3 s
//
// For a high-demand show every customer joins the queue and then polls their
// position until admitted (WaitingRoomPopUp.tsx), so queue status is the most
// frequent request during an on-sale.
//
// Target (ADR-014): queue status p95 < 300 ms with 1,000 virtual users.
//
// The seeded show is already on sale, so customers join the live queue and
// the admission scheduler admits them in batches while the test runs. Once a
// customer is admitted, every poll returns a freshly signed admission token:
// watch whether status latency rises as more customers are admitted.
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { baseUrl, seed, SUMMARY_TREND_STATS } from '../lib/config.js';
import { scenario, think } from '../lib/profiles.js';
import { authHeaders } from '../lib/auth.js';
import { buildSummary } from '../lib/summary.js';

const QUEUE_URL = `${baseUrl('waitingRoom')}/api/waiting-room/queues/${seed.queue.showId}/entries`;
const VALID_STATUSES = ['Waiting', 'Admitted', 'SoldOut'];

const admittedPolls = new Counter('admitted_polls');

export const options = {
  scenarios: { waiting_room: scenario('waitInQueue') },
  summaryTrendStats: SUMMARY_TREND_STATS,
  thresholds: {
    'http_req_duration{name:queue_status}': ['p(95)<300'], // ADR-014 target
    'http_req_failed{name:queue_status}': ['rate<0.01'],
    'http_req_duration{name:queue_join}': ['p(95)<500'],
    'http_req_failed{name:queue_join}': ['rate<0.01'],
    checks: ['rate>0.99'],
  },
};

// Module scope in k6 is per VU, so this tracks whether *this* customer has joined.
let joined = false;

export function waitInQueue() {
  if (!joined) {
    const join = http.post(QUEUE_URL, null, {
      headers: authHeaders(),
      tags: { name: 'queue_join' },
    });
    joined = check(join, {
      'joined queue (200)': (r) => r.status === 200,
      'join returns this show': (r) => r.status === 200 && r.json('showId') === seed.queue.showId,
    });
  }

  const status = http.get(`${QUEUE_URL}/me`, {
    headers: authHeaders(),
    tags: { name: 'queue_status' },
  });

  check(status, {
    'status 200': (r) => r.status === 200,
    'customer is in the queue': (r) => r.status === 200 && VALID_STATUSES.includes(r.json('status')),
    'admitted customers get a token': (r) =>
      r.status !== 200 || r.json('status') !== 'Admitted' || Boolean(r.json('admissionToken')),
  });

  if (status.status === 200 && status.json('status') === 'Admitted') {
    admittedPolls.add(1);
  }

  think(3);
}

export function handleSummary(data) {
  return buildSummary(data, { testName: 'queue' });
}
