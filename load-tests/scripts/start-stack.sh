#!/usr/bin/env bash
# Starts everything the load tests need on this machine:
#   1. infrastructure (Postgres, Kafka) from the repo's docker-compose.yml
#   2. the local token issuer (token-issuer/issuer.mjs)
#   3. the backend services with `dotnet run`, configured for load testing
#
# Usage:  ./scripts/start-stack.sh
# Env:    SERVICES="Catalog Inventory WaitingRoom Gateway"   services to start
#         OTEL=1          export traces/metrics to the Aspire dashboard (localhost:18888)
#         SKIP_BUILD=1    reuse the last Release build
#
# Stop with ./scripts/stop-stack.sh
set -euo pipefail

LOAD_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$LOAD_DIR/.." && pwd)"
RUN_DIR="$LOAD_DIR/.run"
SECRETS_DIR="$LOAD_DIR/.secrets"
mkdir -p "$RUN_DIR" "$SECRETS_DIR"

ISSUER_PORT="${ISSUER_PORT:-9400}"
ISSUER_URL="http://localhost:${ISSUER_PORT}"
SERVICES="${SERVICES:-Catalog Inventory WaitingRoom Gateway}"

# Ports match each service's launchSettings.json "http" profile, which is
# where the gateway's cluster addresses point.
declare -A PORTS=(
  [Identity]=5051 [Catalog]=5142 [Inventory]=5219 [WaitingRoom]=5231
  [Booking]=5005 [Payment]=5006 [Notification]=5007 [Gateway]=5080
)
GATEWAY_DEFAULT_LIMIT_PORT=5081

log() { printf '\033[1;34m[stack]\033[0m %s\n' "$*"; }
fail() { printf '\033[1;31m[stack]\033[0m %s\n' "$*" >&2; exit 1; }

for tool in docker dotnet node openssl curl; do
  command -v "$tool" >/dev/null || fail "'$tool' is required but not installed."
done

wait_for() { # name url timeout_seconds
  local name="$1" url="$2" timeout="${3:-120}" waited=0
  until curl -fsS -o /dev/null "$url"; do
    sleep 2; waited=$((waited + 2))
    if (( waited >= timeout )); then
      fail "$name did not become ready at $url within ${timeout}s (see $RUN_DIR/*.log)"
    fi
  done
  log "$name ready ($url)"
}

# --- 1. Infrastructure --------------------------------------------------------
log "Starting Postgres and Kafka (docker compose)"
(cd "$REPO_ROOT" && docker compose up -d)
# db-init and kafka-init are one-shot containers; `docker wait` returns their exit code.
[[ "$(docker wait tickethive-db-init)" == "0" ]] || fail "database initialisation failed (docker logs tickethive-db-init)"
[[ "$(docker wait tickethive-kafka-init)" == "0" ]] || fail "Kafka topic creation failed (docker logs tickethive-kafka-init)"
log "Infrastructure ready"

# --- 2. Keys and token issuer -------------------------------------------------
# Admission tokens are signed by WaitingRoom and verified by Inventory; both
# refuse to start without a key, so a throwaway pair is generated once.
if [[ ! -f "$SECRETS_DIR/admission-private.pem" ]]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$SECRETS_DIR/admission-private.pem" 2>/dev/null
  openssl pkey -in "$SECRETS_DIR/admission-private.pem" -pubout -out "$SECRETS_DIR/admission-public.pem"
  log "Generated admission-token key pair"
fi

if ! curl -fsS -o /dev/null "$ISSUER_URL/health" 2>/dev/null; then
  log "Starting token issuer on $ISSUER_URL"
  ISSUER_PORT="$ISSUER_PORT" nohup node "$LOAD_DIR/token-issuer/issuer.mjs" >"$RUN_DIR/issuer.log" 2>&1 &
  echo $! >"$RUN_DIR/issuer.pid"
fi
wait_for "Token issuer" "$ISSUER_URL/health" 20

# --- 3. Services --------------------------------------------------------------
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
  log "Building backend (Release)"
  dotnet build "$REPO_ROOT/TicketHive.slnx" -c Release --nologo -v quiet
fi

COMMON_ENV=(
  ASPNETCORE_ENVIRONMENT=Development          # dev-internal-token for service-to-service calls, migrations at startup
  "Jwt__Authority=$ISSUER_URL"                # customer tokens come from the local issuer, not Asgardeo
  Jwt__Audience=tickethive-load-test
  Logging__LogLevel__Default=Warning          # Information-level request logging would distort latency
)
if [[ "${OTEL:-0}" == "1" ]]; then
  COMMON_ENV+=(OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:18889 OTEL_EXPORTER_OTLP_PROTOCOL=grpc)
fi

start_service() { # name port [extra KEY=VALUE ...]
  local name="$1" port="$2"; shift 2
  local project="$REPO_ROOT/services/$name/${name}.Service.csproj"
  [[ "$name" == "Gateway" ]] && project="$REPO_ROOT/services/Gateway/Gateway.csproj"
  local id="${name,,}-${port}"

  if curl -fsS -o /dev/null "http://localhost:${port}/health" 2>/dev/null; then
    log "$name already running on :$port (left as is)"
    return
  fi

  log "Starting $name on :$port"
  env "${COMMON_ENV[@]}" "ASPNETCORE_URLS=http://localhost:${port}" "$@" \
    nohup dotnet run --project "$project" -c Release --no-build --no-launch-profile \
    >"$RUN_DIR/${id}.log" 2>&1 &
  echo $! >"$RUN_DIR/${id}.pid"
}

for svc in $SERVICES; do
  port="${PORTS[$svc]:-}"
  [[ -n "$port" ]] || fail "Unknown service '$svc'"
  case "$svc" in
    Inventory)
      start_service "$svc" "$port" "AdmissionToken__PublicKeyPem=$(cat "$SECRETS_DIR/admission-public.pem")" ;;
    WaitingRoom)
      start_service "$svc" "$port" "AdmissionToken__PrivateKeyPem=$(cat "$SECRETS_DIR/admission-private.pem")" ;;
    Gateway)
      # Two gateways: one with its per-IP limit raised so capacity tests
      # measure the services (a single load generator is one IP), and one
      # with the production default so the rate-limit test can prove it works.
      start_service "$svc" "$port" RateLimiting__PermitLimit=1000000
      start_service "$svc" "$GATEWAY_DEFAULT_LIMIT_PORT" ;;
    *)
      start_service "$svc" "$port" ;;
  esac
done

for svc in $SERVICES; do
  wait_for "$svc" "http://localhost:${PORTS[$svc]}/health/ready" 180
done
[[ " $SERVICES " == *" Gateway "* ]] && wait_for "Gateway (default limit)" "http://localhost:${GATEWAY_DEFAULT_LIMIT_PORT}/health/ready" 180

log "Stack is up. Next: ./scripts/seed.sh, then ./scripts/run.sh <test> <profile>"
