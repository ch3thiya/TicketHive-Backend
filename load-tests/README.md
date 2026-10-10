# TicketHive Load & Performance Tests (k6)

SE3112 Microservice Testing Pipeline. Owner: Manula (Load & Performance Testing).

These tests check TicketHive against the performance and correctness targets in
[ADR-014](../docs/adr/014-quality-targets-and-testing.md) while it is under
on-sale traffic: many customers browsing, polling, queueing and holding tickets
at the same moment.

| Test | Endpoint(s) | Service | What it proves | Target |
|---|---|---|---|---|
| `holds` | `POST /api/inventory/holds` | Inventory | Hold creation stays fast at 1,000 users | p95 < 500 ms (ADR-014) |
| `oversell` | `POST /api/inventory/holds` | Inventory | 500 customers racing for 100 tickets: no oversell, stock conserved, clean 409s, expired holds released | Invariants; release ≤ 30 s (ADR-014) |
| `availability` | `GET /api/inventory/shows/{id}/availability` | Inventory | The most frequent public read stays fast while holds write the same rows | p95 < 300 ms |
| `queue` | `POST …/queues/{id}/entries`, `GET …/entries/me` | WaitingRoom (+ Catalog) | Joining and polling the waiting room at 1,000 users | status p95 < 300 ms (ADR-014) |
| `browse` | `GET /api/catalog/events`, `GET /api/catalog/events/{id}` | Catalog | Home and event pages under load | p95 < 500 ms |
| `rate-limit` | Gateway, default limit | Gateway | One client flooding the gateway gets fast 429s, never 5xx, and never more than the limit | Invariant: ≤ 100 requests per 10 s |

All traffic goes through the YARP gateway by default (`TARGET=gateway`), the path
real customers take. `TARGET=direct` bypasses it to isolate one service.

---

## Quick start

**Prerequisites:** Docker, .NET 10 SDK, Node.js 18+, and [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/)
(on Ubuntu: `sudo apt install k6` after adding Grafana's repository, as the link describes).

```bash
cd load-tests
./scripts/start-stack.sh            # infra + token issuer + Catalog, Inventory, WaitingRoom, Gateway
./scripts/run.sh holds smoke        # seeds data, fetches tokens, runs k6, checks the verdict
./scripts/stop-stack.sh             # add --all to stop Postgres/Kafka too
```

`run.sh <test> <profile>` exits non-zero if any threshold **or** invariant fails.
Each run writes three files to `results/`:

| File | Contents |
|---|---|
| `<test>-<profile>-<time>.md` | The report (also appended to the GitHub Actions job summary in CI) |
| `<test>-<profile>-<time>.json` | Raw k6 data, kept as evidence |
| `<test>-<profile>-<time>-verdict.json` | Pass/fail for thresholds and invariants |

### First run on the real stack: what to check

The scripts were developed and tested against a mock of these endpoints (the
analysis workspace had no .NET SDK). On the first real run, confirm:

1. **Tokens are accepted.** `./scripts/run.sh holds smoke` should show 0 % errors.
   If every `create_hold` fails, open `.run/inventory-5219.log`: a 401 means the
   service didn't accept the local issuer. Check that the service started with
   `Jwt__Authority=http://localhost:9400` (exact match, no trailing slash).
2. **The queue opens.** `./scripts/run.sh queue smoke`: a 404 on `queue_join`
   means WaitingRoom didn't see the seeded show as high-demand. Check the Catalog
   row (`high_demand_threshold > 0`) and the WaitingRoom log.
3. **Seeding works.** `./scripts/seed.sh` on its own should end with "Wrote …/seed.json".

---

## Load profiles

```bash
./scripts/run.sh <test> <profile>
MAX_VUS=500 ./scripts/run.sh holds load       # override the peak
MAX_RATE=300 ./scripts/run.sh holds stress    # stress peak, requests per second
```

| Profile | Shape | Duration | Use |
|---|---|---|---|
| `smoke` | 5 users | 30 s | Does it work at all? CI default |
| `demo` | ramp to 300 users, hold | 55 s | The 2-minute live demo |
| `load` | ramp to 1,000 users, hold 2 min | 3.5 min | The ADR-014 target load |
| `stress` | rising request rate to `MAX_RATE`/s (open model) | 3.5 min | Find the breaking point |
| `spike` | 0 → 1,000 users in 10 s | 50 s | An on-sale moment |
| `soak` | 200 users steady | 15 min | Leaks and slow degradation |

**Closed vs open model.** `smoke`, `demo`, `load`, `spike` and `soak` use virtual
users who wait for each response, then think for ~3 s (the real frontend polls
every 3 s). `stress` uses a fixed arrival rate that keeps sending whatever the
response time. That avoids *coordinated omission*: in a closed model a slow
system receives fewer requests, which hides how slow it really is.

---

## How it works

### Customer identities: the local token issuer

Every customer endpoint validates a JWT, and Inventory allows each customer only
15 hold attempts per 10 s and a few tickets per show. 1,000 virtual users
therefore need 1,000 different identities, which Asgardeo test accounts can't
provide.

`token-issuer/issuer.mjs` is a small OpenID Connect issuer (Node built-ins only).
The services already discover signing keys from whatever `Jwt:Authority` points
at, so `start-stack.sh` points it at the issuer. **No service code changes.**
Before each run, `run.sh` fetches a pool of tokens (`TOKEN_COUNT`, default 5,000)
with a fresh subject prefix, so quotas from one run never affect the next.
k6 loads them once into a `SharedArray`.

- Closed profiles: one virtual user = one customer for the whole test.
- `stress`: requests rotate through the pool so no customer exceeds the per-user limit.

The issuer is for local and CI test stacks only.

### Test data

`scripts/seed.sh` runs before every test and writes `data/seed.json`:

- Catalog rows (event, three shows, ticket categories, plus `BROWSE_EVENTS`
  extra events) inserted with SQL
- Stock created through Inventory's internal endpoint, which accepts the
  `dev-internal-token` placeholder in Development

| Show | Stock | Per-customer limit | Hold time | Used by |
|---|---|---|---|---|
| capacity | 10,000,000 | 1,000,000 | 10 min | holds, availability, rate-limit |
| contention | 100 (`CONTENTION_CAPACITY`) | 1,000 | **1 min** | oversell |
| queue | 10,000,000, high-demand | 6 | 10 min | queue |

### Warm-up before measuring

The first requests after a service starts pay one-off costs: compiling the code
path, opening database connections, the first call to another service. In a
short run, those few slow requests decide the p95. Each test's `setup()` sends
`WARMUP_REQUESTS` (default 5) untimed requests first, tagged `name:warmup` so no
threshold sees them (`lib/warmup.js`). The oversell test warms up on the
capacity show, so no contended ticket is used.

This was found on the first real CI run: every `queue_join` took 530–542 ms
because all five smoke-test users joined at the same instant on services that
hadn't served a request yet. With warm-up, the thresholds measure steady state.
Cold-start latency is still real for the first customers after a deploy:
`WARMUP_REQUESTS=0 ./scripts/run.sh queue smoke` measures it on purpose.

### Two gateways

A single load generator is one IP, and the gateway allows 100 requests per 10 s
per IP. `start-stack.sh` therefore starts the gateway twice:

| Port | Rate limit | Used by |
|---|---|---|
| 5080 | raised (`RateLimiting__PermitLimit=1000000`) | every capacity test |
| 5081 | production default (100 per 10 s) | `rate-limit` |

Both are configured through settings only.

---

## Useful options

| Variable | Default | Effect |
|---|---|---|
| `TARGET` | `gateway` | `direct` sends traffic straight to each service |
| `MAX_VUS` | 1000 (demo: 300) | Peak virtual users |
| `MAX_RATE` | 1000 | Stress-profile peak requests/s |
| `OVERSELL_USERS` | 500 | Customers racing in the oversell test |
| `RELEASE_CHECK` | 1 | `0` skips the ~90 s hold-release wait (quicker demo) |
| `SPOOF_XFF` | 0 | `1` runs the rate-limit exposure check (below) |
| `WARMUP_REQUESTS` | 5 | Untimed requests before measuring; `0` measures a cold system |
| `OTEL` | 0 | `1` on `start-stack.sh` sends traces to the Aspire dashboard (http://localhost:18888) to find bottlenecks |
| `SERVICES` | Catalog Inventory WaitingRoom Gateway | Services `start-stack.sh` starts |

---

## Live demo (2 minutes)

1. Before the slot: `./scripts/start-stack.sh`, then one `./scripts/run.sh holds smoke` to warm up.
2. **0:00–1:05.** `./scripts/run.sh holds demo`. While it ramps to 300 users,
   explain the scenario and the ADR-014 threshold (p95 < 500 ms). When it ends,
   walk through the endpoint table: p95 vs average, error rate, thresholds.
3. **1:05–1:45.** `RELEASE_CHECK=0 ./scripts/run.sh oversell` (about 5 s):
   500 customers, 100 tickets; walk through the invariants.
4. **1:45–2:00.** Show the hold-release result and the pipeline summary from an
   earlier full run.

Keep a full `load` run's report open as a fallback.

---

## Things to watch (and talk about in the viva)

- **Same-machine measurement.** k6 and the services share one laptop's CPU, so
  results are a lower bound on real capacity. Say so; it's the honest answer.
- **Event list has no paging.** `GET /api/catalog/events` returns every
  published event. Raise `BROWSE_EVENTS` and watch `event_list` latency grow.
- **Admission tokens are signed on every poll.** Once a customer is admitted,
  each `entries/me` call signs a new RSA token. Watch whether `queue_status`
  latency rises as more customers are admitted (`admitted_polls` in the raw JSON).
- **The rate limiter trusts `X-Forwarded-For`.** The gateway clears
  `KnownProxies` and uses the last forwarded address (`ForwardLimit = 1`).
  `SPOOF_XFF=1 ./scripts/run.sh rate-limit` sends a fake IP per request; against
  a directly reachable gateway the budget invariant is **expected to fail**. In
  Azure, Container Apps ingress should append the real client IP, which is the
  value the gateway uses. Verify that before presenting it as a production issue.
- **Cold start.** The first requests after a deploy are several times slower
  (≈ 530 ms vs ≈ 20 ms for `queue_join` in CI). Compare `WARMUP_REQUESTS=0` with
  the default to show it; readiness probes or a warm-up step after deployment
  would hide it from customers.
- **Holds and availability share rows.** Run `holds` and `availability` together
  (two terminals) to see read latency under write contention.

---

## Pipeline

`.github/workflows/load-tests.yml` starts the same stack on a GitHub runner
(Docker and .NET are preinstalled), runs every test with the `smoke` profile,
uploads `results/`, and adds each report to the job summary. It also runs on
manual trigger with a profile choice, and as a reusable workflow
(`workflow_call`) for the group pipeline.

## Layout

```
load-tests/
├── token-issuer/issuer.mjs   local OIDC issuer (per-user tokens)
├── scripts/
│   ├── start-stack.sh        infra + issuer + services
│   ├── stop-stack.sh
│   ├── seed.sh               fresh test data per run -> data/seed.json
│   └── run.sh                seed, tokens, k6, verdict
├── lib/
│   ├── config.js             URLs, seed data, summary stats
│   ├── profiles.js           smoke/demo/load/stress/spike/soak
│   ├── auth.js               token pool (SharedArray)
│   ├── warmup.js             untimed warm-up before measuring
│   └── summary.js            console/markdown/JSON report + verdict
└── tests/                    one file per scenario (table at the top)
```
