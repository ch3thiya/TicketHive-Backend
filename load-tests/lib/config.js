// Shared configuration for every load test. All values come from environment
// variables (set by scripts/run.sh) so the same scripts run on a laptop, in CI
// and against either the gateway or the services directly.

// TARGET=gateway (default) sends traffic through the YARP gateway, the path
// real customers take. TARGET=direct bypasses it to isolate one service.
export const TARGET = __ENV.TARGET || 'gateway';
export const PROFILE = __ENV.PROFILE || 'smoke';

const GATEWAY_URL = __ENV.GATEWAY_URL || 'http://localhost:5080';

const DIRECT_URLS = {
  catalog: __ENV.CATALOG_URL || 'http://localhost:5142',
  inventory: __ENV.INVENTORY_URL || 'http://localhost:5219',
  waitingRoom: __ENV.WAITING_ROOM_URL || 'http://localhost:5231',
};

export function baseUrl(service) {
  return TARGET === 'direct' ? DIRECT_URLS[service] : GATEWAY_URL;
}

// Seed data written by scripts/seed.sh. open() only works in the init
// context, so it is read once here when the test loads.
export const seed = JSON.parse(open(__ENV.SEED_FILE || '../data/seed.json'));

// Statistics reported for every timing metric. p(95) is what the ADR-014
// targets are written against; p(99) shows the tail beyond it.
export const SUMMARY_TREND_STATS = ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max', 'count'];
