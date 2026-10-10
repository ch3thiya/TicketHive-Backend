// Warm-up before measurement.
//
// The first requests after a service starts pay one-off costs: JIT compiling
// the code path, opening database connections, the first call to another
// service. In a short run those few slow requests decide the p95. Each test's
// setup() therefore sends WARMUP_REQUESTS untimed requests first. They are
// tagged name:warmup, so no endpoint threshold sees them.
//
// Cold-start latency is real (the first customers after a deploy feel it),
// it just isn't the steady-state performance these thresholds judge.
// WARMUP_REQUESTS=0 measures a cold system on purpose.
export const WARMUP_REQUESTS = Number(__ENV.WARMUP_REQUESTS ?? 5);
export const WARMUP_TAGS = { name: 'warmup' };

export function warmUp(request) {
  for (let i = 0; i < WARMUP_REQUESTS; i++) {
    request(i);
  }
}
