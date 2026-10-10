#!/usr/bin/env bash
# Deletes load-test data from the Inventory database: holds, hold items,
# customer quotas and stock of the shows that seed.sh created. Hold-heavy runs
# leave ~50,000 holds each, and the expiry sweeper (200 per 10 s) needs ~40
# minutes to release them, which would distort the next measured run.
#
# show_rules rows are kept (they are how the shows are found, and are tiny).
# Nothing else is touched: other organizers' shows, and Catalog, are left alone.
#
# Usage:  ./scripts/cleanup.sh                  dry run: prints the statements and row counts
#         ./scripts/cleanup.sh --execute        really deletes
#         ./scripts/cleanup.sh --legacy-today [--execute]
#             one-off for data seeded before the fixed organizer id existed: finds
#             today's seed.sh shows through Catalog (events marked "Seeded by
#             load-tests/scripts/seed.sh" and created since local midnight) and only
#             deletes holds created since local midnight. Rows from earlier days are
#             never selected.
# Env:    PG_CONTAINER=tickethive-postgres
#
# Do not use SKIP_SEED=1 with a seed.json whose rows were cleaned up.
set -euo pipefail

# shellcheck source=common.sh
source "$(dirname "${BASH_SOURCE[0]}")/common.sh"
PG_CONTAINER="${PG_CONTAINER:-tickethive-postgres}"

EXECUTE=0
LEGACY=0
for arg in "$@"; do
  case "$arg" in
    --execute) EXECUTE=1 ;;
    --legacy-today) LEGACY=1 ;;
    *) echo "Unknown option: $arg" >&2; exit 2 ;;
  esac
done

log() { printf '\033[1;34m[cleanup]\033[0m %s\n' "$*" >&2; }
psql_inv() { docker exec -i "$PG_CONTAINER" psql -U postgres -d tickethive_inventory -v ON_ERROR_STOP=1 "$@"; }

if [[ "$LEGACY" == "1" ]]; then
  SINCE="$(date -u -d 'today 00:00' +%Y-%m-%dT%H:%M:%SZ)"   # local midnight, as UTC
  # Catalog is only read here, to learn which show ids seed.sh created today.
  SHOW_IDS="$(docker exec -i "$PG_CONTAINER" psql -At -U postgres -d tickethive_catalog -v ON_ERROR_STOP=1 -c \
    "SELECT s.id FROM shows s JOIN events e ON e.id = s.event_id
      WHERE e.description LIKE 'Seeded by load-tests/scripts/seed.sh%' AND e.created_at >= '$SINCE'")"
  [[ -n "$SHOW_IDS" ]] || { log "No load-test shows created since $SINCE; nothing to do."; exit 0; }
  VALUES="$(printf "('%s')," $SHOW_IDS)"
  SHOWS_SQL="INSERT INTO load_shows SELECT show_id FROM show_rules WHERE show_id IN (SELECT v::uuid FROM (VALUES ${VALUES%,}) AS t(v));"
  log "Legacy mode: shows seeded since $SINCE; holds created before then are never deleted."
else
  SINCE="-infinity"
  SHOWS_SQL="INSERT INTO load_shows SELECT show_id FROM show_rules WHERE organizer_id = '$LOAD_TEST_ORGANIZER_ID';"
  log "Selecting shows of organizer $LOAD_TEST_ORGANIZER_ID"
fi

SETUP="CREATE TEMP TABLE load_shows (show_id uuid PRIMARY KEY);
$SHOWS_SQL"

DELETE_HOLD_ITEMS="DELETE FROM hold_items WHERE hold_id IN (SELECT id FROM holds WHERE show_id IN (SELECT show_id FROM load_shows) AND created_at >= '$SINCE');"
DELETE_HOLDS="DELETE FROM holds WHERE show_id IN (SELECT show_id FROM load_shows) AND created_at >= '$SINCE';"
DELETE_QUOTAS="DELETE FROM customer_quotas WHERE show_id IN (SELECT show_id FROM load_shows);"
DELETE_STOCK="DELETE FROM stock WHERE show_id IN (SELECT show_id FROM load_shows);"

if [[ "$EXECUTE" == "0" ]]; then
  log "DRY RUN. Statements that --execute would run (in one transaction):"
  printf '%s\n%s\n%s\n%s\n%s\n' "$SHOWS_SQL" "$DELETE_HOLD_ITEMS" "$DELETE_HOLDS" "$DELETE_QUOTAS" "$DELETE_STOCK"
  log "Rows they would affect:"
  psql_inv <<SQL
$SETUP
SELECT 'load-test shows'      AS what, count(*) AS rows FROM load_shows
UNION ALL SELECT 'hold_items',        count(*) FROM hold_items WHERE hold_id IN (SELECT id FROM holds WHERE show_id IN (SELECT show_id FROM load_shows) AND created_at >= '$SINCE')
UNION ALL SELECT 'holds',             count(*) FROM holds WHERE show_id IN (SELECT show_id FROM load_shows) AND created_at >= '$SINCE'
UNION ALL SELECT 'customer_quotas',   count(*) FROM customer_quotas WHERE show_id IN (SELECT show_id FROM load_shows)
UNION ALL SELECT 'stock',             count(*) FROM stock WHERE show_id IN (SELECT show_id FROM load_shows)
UNION ALL SELECT 'NOT selected: holds',  count(*) FROM holds WHERE NOT (show_id IN (SELECT show_id FROM load_shows) AND created_at >= '$SINCE')
UNION ALL SELECT 'NOT selected: stock',  count(*) FROM stock WHERE show_id NOT IN (SELECT show_id FROM load_shows);
SQL
  log "Nothing deleted. Re-run with --execute."
  exit 0
fi

log "Deleting"
psql_inv <<SQL
BEGIN;
$SETUP
$DELETE_HOLD_ITEMS
$DELETE_HOLDS
$DELETE_QUOTAS
$DELETE_STOCK
COMMIT;
SQL
# Dead rows from a large delete would otherwise slow the next run's queries.
docker exec -i "$PG_CONTAINER" psql -q -U postgres -d tickethive_inventory -c "VACUUM (ANALYZE) holds, hold_items, customer_quotas, stock;"
log "Done"
