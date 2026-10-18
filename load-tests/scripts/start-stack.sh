#!/usr/bin/env bash
# Starts everything the load tests need on this machine:
#   1. infrastructure (Postgres, Kafka) from the repo's docker-compose.yml
#   2. the local token issuer (token-issuer/issuer.mjs)
#   3. the built backend service assemblies, configured for load testing
#
# Usage:  ./scripts/start-stack.sh
# Env:    SERVICES="Identity Catalog Inventory WaitingRoom Gateway"   services to start
#         OTEL=1          export traces/metrics to the Aspire dashboard (localhost:18888)
#         SKIP_BUILD=1    reuse the last Release build
#         INVENTORY_MAX_POOL=20   Inventory database pool size for the test stack
#         POSTGRES_HOST_PORT=55432 local host port used by the isolated database
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
SERVICES="${SERVICES:-Identity Catalog Inventory WaitingRoom Gateway}"
POSTGRES_HOST_PORT="${POSTGRES_HOST_PORT:-55432}"

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
  until curl -fsS --connect-timeout 2 --max-time 5 -o /dev/null "$url"; do
    sleep 2; waited=$((waited + 2))
    if (( waited >= timeout )); then
      local port="${url#*localhost:}"
      port="${port%%/*}"
      for service_log in "$RUN_DIR"/*-"$port".log; do
        if [[ -f "$service_log" ]]; then
          printf '\n[stack] Last 100 lines of %s:\n' "$service_log" >&2
          tail -100 "$service_log" >&2
        fi
      done
      fail "$name did not become ready at $url within ${timeout}s (see $RUN_DIR/*.log)"
    fi
  done
  log "$name ready ($url)"
}

# --- 1. Infrastructure --------------------------------------------------------
log "Starting Postgres and Kafka (docker compose)"
(cd "$REPO_ROOT" && POSTGRES_HOST_PORT="$POSTGRES_HOST_PORT" docker compose up -d)
# db-init and kafka-init are one-shot containers; `docker wait` returns their exit code.
if [[ "$(docker wait tickethive-db-init)" != "0" ]]; then
  log "Database initialisation failed once; retrying after the Docker network is ready"
  (cd "$REPO_ROOT" && POSTGRES_HOST_PORT="$POSTGRES_HOST_PORT" docker compose up -d --no-deps --force-recreate db-init)
  [[ "$(docker wait tickethive-db-init)" == "0" ]] || fail "database initialisation failed (docker logs tickethive-db-init)"
fi
if [[ "$(docker wait tickethive-kafka-init)" != "0" ]]; then
  log "Kafka topic initialisation failed once; retrying after the Docker network is ready"
  (cd "$REPO_ROOT" && POSTGRES_HOST_PORT="$POSTGRES_HOST_PORT" docker compose up -d --no-deps --force-recreate kafka-init)
  [[ "$(docker wait tickethive-kafka-init)" == "0" ]] || fail "Kafka topic creation failed (docker logs tickethive-kafka-init)"
fi
log "Infrastructure ready"

# --- 2. Keys and token issuer -------------------------------------------------
# Admission tokens are signed by WaitingRoom and verified by Inventory; both
# refuse to start without a key, so a throwaway pair is generated once.
if [[ ! -f "$SECRETS_DIR/admission-private.pem" ]]; then
  openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$SECRETS_DIR/admission-private.pem" 2>/dev/null
  openssl pkey -in "$SECRETS_DIR/admission-private.pem" -pubout -out "$SECRETS_DIR/admission-public.pem"
  log "Generated admission-token key pair"
fi

if ! curl -fsS --connect-timeout 1 --max-time 2 -o /dev/null "$ISSUER_URL/health" 2>/dev/null; then
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
database_connection() { # database
  printf 'Host=localhost;Port=%s;Database=%s;Username=postgres;Password=postgres' "$POSTGRES_HOST_PORT" "$1"
}
if [[ "${OTEL:-0}" == "1" ]]; then
  COMMON_ENV+=(OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:18889 OTEL_EXPORTER_OTLP_PROTOCOL=grpc)
fi

start_service() { # name port [extra KEY=VALUE ...]
  local name="$1" port="$2"; shift 2
  local id="${name,,}-${port}"
  local assembly="$REPO_ROOT/services/$name/bin/Release/net10.0/${name}.Service.dll"
  if [[ "$name" == "Gateway" ]]; then
    assembly="$REPO_ROOT/services/Gateway/bin/Release/net10.0/Gateway.dll"
  fi

  if curl -fsS --connect-timeout 1 --max-time 2 -o /dev/null "http://localhost:${port}/health" 2>/dev/null; then
    log "$name already running on :$port (left as is)"
    return
  fi

  log "Starting $name on :$port"
  env "${COMMON_ENV[@]}" "ASPNETCORE_URLS=http://localhost:${port}" "$@" \
    nohup dotnet "$assembly" --contentRoot "$REPO_ROOT/services/$name" \
    >"$RUN_DIR/${id}.log" 2>&1 &
  echo $! >"$RUN_DIR/${id}.pid"
}

for svc in $SERVICES; do
  port="${PORTS[$svc]:-}"
  [[ -n "$port" ]] || fail "Unknown service '$svc'"
  case "$svc" in
    Identity)
      # Identity validates its Asgardeo administration credentials at startup.
      # The load suite only calls its local internal organizer-status endpoint,
      # so use unmistakably synthetic values and an unreachable loopback base
      # URL instead of requiring real credentials on developer or CI machines.
      start_service "$svc" "$port" \
        "ConnectionStrings__DefaultConnection=$(database_connection tickethive_identity)" \
        Wso2__BaseUrl=http://127.0.0.1:1/ \
        Wso2__AdminUsername=load-test-only \
        Wso2__AdminPassword=load-test-only \
        Wso2__M2mClientId=load-test-only \
        Wso2__M2mClientSecret=load-test-only ;;
    Catalog)
      start_service "$svc" "$port" "ConnectionStrings__DefaultConnection=$(database_connection tickethive_catalog)" ;;
    Inventory)
      inventory_connection="$(database_connection tickethive_inventory)"
      inventory_env=(
        "AdmissionToken__PublicKeyPem=$(cat "$SECRETS_DIR/admission-public.pem")"
        "ConnectionStrings__DefaultConnection=${inventory_connection};Maximum Pool Size=${INVENTORY_MAX_POOL:-20};Timeout=30"
      )
      if [[ -n "${INVENTORY_MAX_POOL:-}" ]]; then
        [[ "$INVENTORY_MAX_POOL" =~ ^[1-9][0-9]*$ ]] || fail "INVENTORY_MAX_POOL must be a positive integer"
        log "Inventory pool limited to $INVENTORY_MAX_POOL connections (Timeout=30)"
      fi
      start_service "$svc" "$port" "${inventory_env[@]}" ;;
    WaitingRoom)
      start_service "$svc" "$port" \
        "AdmissionToken__PrivateKeyPem=$(cat "$SECRETS_DIR/admission-private.pem")" \
        "ConnectionStrings__DefaultConnection=$(database_connection tickethive_waitingroom)" ;;
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
if [[ " $SERVICES " == *" Gateway "* ]]; then
  wait_for "Gateway (default limit)" "http://localhost:${GATEWAY_DEFAULT_LIMIT_PORT}/health/ready" 180
fi

log "Stack is up. Next: ./scripts/seed.sh, then ./scripts/run.sh <test> <profile>"
