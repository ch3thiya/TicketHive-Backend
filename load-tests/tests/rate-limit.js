// Gateway rate limiter under a flood from one client
//
// The gateway allows each client IP RATE_LIMIT_PERMITS requests per
// RATE_LIMIT_WINDOW_S seconds (appsettings: 100 per 10 s) and rejects the rest
// with 429. This test floods a gateway running that default configuration
// (port 5081, started by start-stack.sh) from a single load generator and
// checks that the limiter protects the services instead of passing the flood on:
//
//   - excess requests get a fast 429, never a 5xx or a timeout
//   - the number let through never exceeds the configured budget
//
// Every other test uses the gateway on port 5080, whose limit is raised so a
// single load generator (one IP) can reach 1,000 virtual users.
//
// SPOOF_XFF=1 is an exposure check. The limiter partitions by client IP, which
// the gateway takes from X-Forwarded-For, trusting any sender (KnownProxies
// cleared, ForwardLimit = 1, so the last value wins). With this flag every
// request claims a different IP. Against a directly reachable gateway the
// budget invariant is EXPECTED to fail: the limit can be bypassed. In Azure the
// Container Apps ingress should append the real client IP as the last value,
// which is the one the gateway uses, so the spoofed value would be ignored.
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';
import { seed, SUMMARY_TREND_STATS } from '../lib/config.js';
import { scenario } from '../lib/profiles.js';
import { buildSummary } from '../lib/summary.js';

const GATEWAY_URL = __ENV.GATEWAY_DEFAULT_LIMIT_URL || 'http://localhost:5081';
const PERMITS = Number(__ENV.RATE_LIMIT_PERMITS || 100);
const WINDOW_S = Number(__ENV.RATE_LIMIT_WINDOW_S || 10);
const SPOOF_XFF = __ENV.SPOOF_XFF === '1';
const URL = `${GATEWAY_URL}/api/inventory/shows/${seed.capacity.showId}/availability`;

const randomIp = () =>
  `10.${Math.floor(Math.random() * 256)}.${Math.floor(Math.random() * 256)}.${1 + Math.floor(Math.random() * 254)}`;

const allowed = new Counter('allowed_requests');
const rejected = new Counter('rejected_requests');
const serverErrors = new Counter('server_errors');
const unexpectedResponses = new Counter('unexpected_responses');
const rejectionLatency = new Trend('rejection_latency', true);

export const options = {
  scenarios: { flood: scenario('flood') },
  summaryTrendStats: SUMMARY_TREND_STATS,
  thresholds: {
    server_errors: ['count==0'],
    unexpected_responses: ['count==0'], // e.g. a 404 means the test is misconfigured
    rejected_requests: ['count>0'], // the limiter must actually engage
    rejection_latency: ['p(95)<100'], // a 429 should cost the gateway almost nothing
    'http_req_duration{name:flood}': ['p(99)<1000'],
    'http_req_failed{name:flood}': ['rate<0.001'],
  },
};

export function flood() {
  const res = http.get(URL, {
    headers: SPOOF_XFF ? { 'X-Forwarded-For': randomIp() } : {},
    tags: { name: 'flood' },
    responseCallback: http.expectedStatuses(200, 429),
  });

  if (res.status === 200) allowed.add(1);
  else if (res.status === 429) {
    rejected.add(1);
    rejectionLatency.add(res.timings.duration);
  } else if (res.status >= 500) serverErrors.add(1);
  else unexpectedResponses.add(1);

  check(res, { 'allowed (200) or limited (429)': (r) => r.status === 200 || r.status === 429 });
  sleep(0.05); // ~20 requests/s per VU: far above the 10/s budget for the whole IP
}

export function handleSummary(data) {
  const durationS = data.state.testRunDurationMs / 1000;
  // Fixed windows: a run of d seconds can touch at most ceil(d / window) + 1 windows.
  const windows = Math.ceil(durationS / WINDOW_S) + 1;
  const budget = windows * PERMITS;
  const passed = data.metrics.allowed_requests?.values.count || 0;
  const limited = data.metrics.rejected_requests?.values.count || 0;

  return buildSummary(data, {
    testName: SPOOF_XFF ? 'rate-limit-spoofed-ip' : 'rate-limit',
    invariants: [
      {
        name: 'Limiter holds the budget',
        ok: passed <= budget,
        detail: `${passed} let through, ${limited} rejected; budget ${budget} (${PERMITS} per ${WINDOW_S} s x ${windows} windows in ${durationS.toFixed(0)} s)`,
      },
    ],
  });
}
