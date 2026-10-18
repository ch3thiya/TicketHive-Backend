// Customer identities for virtual users.
//
// scripts/run.sh fetches a pool of tokens from the local issuer into
// data/tokens.json before k6 starts. A SharedArray holds them once in memory
// for all VUs; passing them through setup() would copy the whole pool into
// every VU (thousands of VUs x megabytes of tokens).
import { SharedArray } from 'k6/data';
import exec from 'k6/execution';
import { IS_OPEN_MODEL } from './profiles.js';

const tokens = new SharedArray('customer tokens', () =>
  JSON.parse(open(__ENV.TOKENS_FILE || '../data/tokens.json')).tokens.map((t) => t.token),
);

// Closed model: one VU is one customer for the whole test, as in real life.
// Open model: requests rotate through the pool so no single customer exceeds
// Inventory's per-user limit of 15 hold attempts per 10 seconds.
export function currentToken() {
  const index = IS_OPEN_MODEL ? exec.scenario.iterationInTest : exec.vu.idInTest - 1;
  if (!IS_OPEN_MODEL && index >= tokens.length) {
    exec.test.abort(`Only ${tokens.length} tokens for ${index + 1} VUs; raise TOKEN_COUNT in run.sh`);
  }
  return tokens[index % tokens.length];
}

// Identities for warm-up requests in setup(), taken from the end of the pool
// so they don't collide with the customers the VUs play.
export function warmupHeaders(n, extra = {}) {
  return { Authorization: `Bearer ${tokens[tokens.length - 1 - (n % 10)]}`, ...extra };
}

export function authHeaders(extra = {}) {
  return { Authorization: `Bearer ${currentToken()}`, ...extra };
}
