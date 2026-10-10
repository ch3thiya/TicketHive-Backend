#!/usr/bin/env bash
# Creates fresh test data for one load-test run and writes data/seed.json,
# which every k6 test reads.
#
#   capacity   show with effectively unlimited stock: measures hold latency
#              without stock or the per-customer limit getting in the way
#   contention show with only CONTENTION_CAPACITY tickets and 1-minute holds:
#              proves no oversell and hold release end to end (must be fresh
#              each run, so run.sh seeds before every test)
#   queue      high-demand show already on sale: drives the waiting room
#   browse     published events in Catalog for the browse test
#
# Catalog rows are inserted with SQL (no organizer/admin flow needed); stock
# is created through Inventory's internal endpoint, which accepts the
# `dev-internal-token` placeholder in Development.
#
# Usage:  ./scripts/seed.sh
# Env:    CONTENTION_CAPACITY=100  BROWSE_EVENTS=50  SEED_CATALOG=1
set -euo pipefail

LOAD_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA_DIR="$LOAD_DIR/data"
mkdir -p "$DATA_DIR"

INVENTORY_URL="${INVENTORY_URL:-http://localhost:5219}"
PG_CONTAINER="${PG_CONTAINER:-tickethive-postgres}"
CONTENTION_CAPACITY="${CONTENTION_CAPACITY:-100}"
CAPACITY_STOCK="${CAPACITY_STOCK:-10000000}"
BROWSE_EVENTS="${BROWSE_EVENTS:-50}"
SEED_CATALOG="${SEED_CATALOG:-1}"

uuid() { cat /proc/sys/kernel/random/uuid; }
log() { printf '\033[1;34m[seed]\033[0m %s\n' "$*"; }

ORGANIZER_ID="$(uuid)"
EVENT_ID="$(uuid)"
CAPACITY_SHOW="$(uuid)";   CAPACITY_CATEGORY="$(uuid)"
CONTENTION_SHOW="$(uuid)"; CONTENTION_CATEGORY="$(uuid)"
QUEUE_SHOW="$(uuid)";      QUEUE_CATEGORY="$(uuid)"

# --- Catalog: one main event with the three shows, plus extra events to browse ---
if [[ "$SEED_CATALOG" == "1" ]]; then
  log "Seeding Catalog ($((BROWSE_EVENTS + 1)) published events)"
  docker exec -i "$PG_CONTAINER" psql -q -U postgres -d tickethive_catalog -v ON_ERROR_STOP=1 <<SQL
BEGIN;
INSERT INTO events (id, organizer_id, name, description, category, event_date, event_time, status)
VALUES ('$EVENT_ID', '$ORGANIZER_ID', 'Load Test Concert', 'Seeded by load-tests/scripts/seed.sh',
        'Concert', CURRENT_DATE + 30, '19:00', 'Published');

INSERT INTO shows (id, event_id, show_date, show_time, on_sale_at, high_demand_threshold, status) VALUES
  ('$CAPACITY_SHOW',   '$EVENT_ID', CURRENT_DATE + 30, '19:00', now() - interval '1 hour',   NULL, 'Active'),
  ('$CONTENTION_SHOW', '$EVENT_ID', CURRENT_DATE + 31, '19:00', now() - interval '1 hour',   NULL, 'Active'),
  -- high_demand_threshold > 0 is what makes Catalog report the show as
  -- high-demand, which is what makes WaitingRoom open a queue for it.
  ('$QUEUE_SHOW',      '$EVENT_ID', CURRENT_DATE + 32, '19:00', now() - interval '1 minute', 1,    'Active');

INSERT INTO ticket_categories (id, show_id, name, price, capacity) VALUES
  ('$CAPACITY_CATEGORY',   '$CAPACITY_SHOW',   'General', 2500.00, $CAPACITY_STOCK),
  ('$CONTENTION_CATEGORY', '$CONTENTION_SHOW', 'General', 2500.00, $CONTENTION_CAPACITY),
  ('$QUEUE_CATEGORY',      '$QUEUE_SHOW',      'General', 2500.00, $CAPACITY_STOCK);

INSERT INTO events (organizer_id, name, description, category, event_date, event_time, status)
SELECT '$ORGANIZER_ID', 'Load Test Event ' || n, 'Seeded browse data', 'Concert',
       CURRENT_DATE + (n % 60), '19:00', 'Published'
FROM generate_series(1, $BROWSE_EVENTS) AS n;
COMMIT;
SQL
fi

# --- Inventory: stock for each show -------------------------------------------
init_stock() { # show_id category_id capacity max_per_customer hold_minutes high_demand threshold
  local body
  body=$(cat <<JSON
{
  "organizerId": "$ORGANIZER_ID",
  "onSaleAt": null,
  "maxPerCustomer": $4,
  "holdMinutes": $5,
  "highDemand": $6,
  "highDemandThreshold": $7,
  "categories": [
    { "categoryId": "$2", "capacity": $3, "unitPrice": 2500.00, "currency": "LKR", "allocationMode": "GA" }
  ]
}
JSON
)
  local status
  status=$(curl -sS -o "$DATA_DIR/.last-response.json" -w '%{http_code}' -X PUT \
    "$INVENTORY_URL/internal/inventory/shows/$1" \
    -H 'Authorization: Bearer dev-internal-token' \
    -H 'Content-Type: application/json' \
    -d "$body")
  if [[ "$status" != "200" ]]; then
    echo "Inventory stock initialisation failed for show $1 (HTTP $status):" >&2
    cat "$DATA_DIR/.last-response.json" >&2
    exit 1
  fi
}

log "Seeding Inventory stock"
# The capacity show's per-customer limit is set far above what one virtual
# user can reach, so the test measures the hold path rather than the quota.
# The contention show's holds last 1 minute so oversell.js can watch them
# expire and be released (ADR-014: released within 30 s of expiry).
#          show               category               stock                  limit   minutes high_demand threshold
init_stock "$CAPACITY_SHOW"   "$CAPACITY_CATEGORY"   "$CAPACITY_STOCK"      1000000 10      false       null
init_stock "$CONTENTION_SHOW" "$CONTENTION_CATEGORY" "$CONTENTION_CAPACITY" 1000    1       false       null
init_stock "$QUEUE_SHOW"      "$QUEUE_CATEGORY"      "$CAPACITY_STOCK"      6       10      true        1

cat >"$DATA_DIR/seed.json" <<JSON
{
  "seededAt": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "eventId": "$EVENT_ID",
  "capacity":   { "showId": "$CAPACITY_SHOW",   "categoryId": "$CAPACITY_CATEGORY",   "stock": $CAPACITY_STOCK },
  "contention": { "showId": "$CONTENTION_SHOW", "categoryId": "$CONTENTION_CATEGORY", "stock": $CONTENTION_CAPACITY },
  "queue":      { "showId": "$QUEUE_SHOW",      "categoryId": "$QUEUE_CATEGORY" }
}
JSON
rm -f "$DATA_DIR/.last-response.json"
log "Wrote $DATA_DIR/seed.json"
