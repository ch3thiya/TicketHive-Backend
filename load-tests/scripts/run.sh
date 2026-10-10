#!/usr/bin/env bash
# Runs one load test end to end:
#   1. seeds fresh test data          (scripts/seed.sh)
#   2. fetches customer tokens        (token issuer -> data/tokens.json)
#   3. runs k6
#   4. fails if a threshold OR an invariant failed, and appends the report to
#      the GitHub Actions job summary when running in CI
#
# Usage:  ./scripts/run.sh <test> [profile]
#   tests:    holds | oversell | availability | queue | browse | rate-limit
#   profiles: smoke (default) | demo | load | stress | spike | soak
#
# Env:    TARGET=gateway|direct  MAX_VUS=1000  TOKEN_COUNT=5000  SKIP_SEED=1
#         OVERSELL_USERS=500  RELEASE_CHECK=0  SPOOF_XFF=1
#         any other k6 flags can be passed in K6_ARGS, e.g. K6_ARGS="--out json=raw.json"
set -euo pipefail

LOAD_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST="${1:-}"
PROFILE="${2:-smoke}"
ISSUER_URL="${ISSUER_URL:-http://localhost:9400}"
TOKEN_COUNT="${TOKEN_COUNT:-5000}"

TESTS="holds oversell availability queue browse rate-limit"
if [[ -z "$TEST" || ! -f "$LOAD_DIR/tests/$TEST.js" ]]; then
  echo "Usage: $0 <test> [profile]"
  echo "  tests:    $TESTS"
  echo "  profiles: smoke demo load stress spike soak"
  exit 2
fi
command -v k6 >/dev/null || { echo "k6 is not installed: https://grafana.com/docs/k6/latest/set-up/install-k6/" >&2; exit 2; }

mkdir -p "$LOAD_DIR/data" "$LOAD_DIR/results"

# 1. Fresh data every run. The oversell test needs it (its stock is used up),
#    and the others are kept independent of whatever ran before.
if [[ "${SKIP_SEED:-0}" != "1" ]]; then
  "$LOAD_DIR/scripts/seed.sh"
fi

# 2. Tokens: a new subject prefix per run, so per-customer quotas and rate
#    limits from an earlier run never carry over.
curl -fsS "$ISSUER_URL/tokens?count=$TOKEN_COUNT&prefix=load-$(date +%s)-" -o "$LOAD_DIR/data/tokens.json" \
  || { echo "Could not get tokens from $ISSUER_URL (is the stack running? ./scripts/start-stack.sh)" >&2; exit 1; }

# 3. k6
RUN_ID="${TEST}-${PROFILE}-$(date +%Y%m%d-%H%M%S)"
RESULTS_PREFIX="$LOAD_DIR/results/$RUN_ID"
k6_status=0
export K6_NO_USAGE_REPORT=true  # no anonymous usage stats to Grafana
# shellcheck disable=SC2086  # K6_ARGS is intentionally word-split
k6 run \
  -e PROFILE="$PROFILE" \
  -e TARGET="${TARGET:-gateway}" \
  -e SEED_FILE="$LOAD_DIR/data/seed.json" \
  -e TOKENS_FILE="$LOAD_DIR/data/tokens.json" \
  -e RESULTS_PREFIX="$RESULTS_PREFIX" \
  ${K6_ARGS:-} \
  "$LOAD_DIR/tests/$TEST.js" || k6_status=$?

# 4. Verdict: k6 exits non-zero for a failed threshold (99) or a script error;
#    invariants are judged from the verdict file written by handleSummary.
verdict_ok=$(node -e "process.stdout.write(String(require(process.argv[1]).passed))" "$RESULTS_PREFIX-verdict.json" 2>/dev/null || echo false)

if [[ -n "${GITHUB_STEP_SUMMARY:-}" && -f "$RESULTS_PREFIX.md" ]]; then
  cat "$RESULTS_PREFIX.md" >>"$GITHUB_STEP_SUMMARY"
fi

echo "Report: $RESULTS_PREFIX.md (raw data: .json, verdict: -verdict.json)"
if [[ "$k6_status" != "0" || "$verdict_ok" != "true" ]]; then
  echo "Load test '$TEST' ($PROFILE) FAILED (k6 exit $k6_status, verdict passed=$verdict_ok)" >&2
  exit 1
fi
echo "Load test '$TEST' ($PROFILE) passed"
