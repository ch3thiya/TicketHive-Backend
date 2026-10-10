// Load profiles shared by every test. A profile describes the shape of the
// traffic; each test decides what a single user does.
//
//   smoke   a handful of users: does the script and system work at all? (CI)
//   demo    ramp to 300 users in under a minute (the live demo)
//   load    ramp to 1,000 users and hold: the ADR-014 target load
//   stress  open model, rising request rate until something gives
//   spike   0 -> 1,000 users in 10 s: an on-sale moment
//   soak    steady moderate load for 15 min: leaks and slow degradation
//
// "Closed" profiles (ramping-vus) model users who wait for a response before
// acting again. The "open" profile (ramping-arrival-rate) keeps sending at a
// fixed rate whatever the response time, which avoids coordinated omission:
// a slow system can't quietly reduce the load it receives.
import { sleep } from 'k6';
import { PROFILE } from './config.js';

const MAX_VUS = Number(__ENV.MAX_VUS || 1000);
const MAX_RATE = Number(__ENV.MAX_RATE || 1000); // stress: peak requests per second

const PROFILES = {
  smoke: {
    executor: 'constant-vus',
    vus: 5,
    duration: '30s',
  },
  demo: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '20s', target: Number(__ENV.MAX_VUS || 300) },
      { duration: '30s', target: Number(__ENV.MAX_VUS || 300) },
      { duration: '5s', target: 0 },
    ],
    gracefulRampDown: '5s',
  },
  load: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '1m', target: MAX_VUS },
      { duration: '2m', target: MAX_VUS },
      { duration: '30s', target: 0 },
    ],
    gracefulRampDown: '10s',
  },
  stress: {
    executor: 'ramping-arrival-rate',
    startRate: Math.ceil(MAX_RATE * 0.05),
    timeUnit: '1s',
    preAllocatedVUs: 200,
    maxVUs: Number(__ENV.MAX_VUS || 2000),
    stages: [
      { duration: '1m', target: Math.ceil(MAX_RATE * 0.2) },
      { duration: '1m', target: Math.ceil(MAX_RATE * 0.5) },
      { duration: '1m', target: MAX_RATE },
      { duration: '30s', target: 0 },
    ],
  },
  spike: {
    executor: 'ramping-vus',
    startVUs: 0,
    stages: [
      { duration: '10s', target: MAX_VUS },
      { duration: '30s', target: MAX_VUS },
      { duration: '10s', target: 0 },
    ],
    gracefulRampDown: '5s',
  },
  soak: {
    executor: 'constant-vus',
    vus: Number(__ENV.MAX_VUS || 200),
    duration: __ENV.SOAK_DURATION || '15m',
  },
};

export const IS_OPEN_MODEL = PROFILES[PROFILE]?.executor === 'ramping-arrival-rate';

export function scenario(exec, overrides = {}) {
  const profile = PROFILES[PROFILE];
  if (!profile) {
    throw new Error(`Unknown PROFILE '${PROFILE}'. Use one of: ${Object.keys(PROFILES).join(', ')}`);
  }
  return { ...profile, exec, ...overrides };
}

// Think time between a user's actions. Real clients poll every 3 s
// (EventDetail.tsx and WaitingRoomPopUp.tsx). In the open model the arrival
// rate already sets the pace, so users don't pause.
export function think(seconds = 3) {
  if (!IS_OPEN_MODEL) {
    sleep(seconds * (0.8 + Math.random() * 0.4)); // +/-20% so users don't move in lockstep
  }
}
