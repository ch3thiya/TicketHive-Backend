#!/usr/bin/env bash
# Shared by seed.sh and cleanup.sh (sourced, not run).

# Every show that seed.sh creates belongs to this organizer, so cleanup.sh can
# find load-test rows in the Inventory database without touching anything else.
# Never use this id for real data.
LOAD_TEST_ORGANIZER_ID="10ad7e57-0000-4000-8000-000000000001"

# Load tests are intentionally restricted to loopback targets. This prevents a
# copied command, stale shell variable, or CI secret from directing destructive
# traffic at Azure or another shared environment. Remote performance testing is
# outside this assessment pipeline and must use a separately reviewed script.
assert_local_url() { # label url
  local label="$1" url="$2"
  case "$url" in
    http://localhost|http://localhost:*|http://127.0.0.1|http://127.0.0.1:*|http://\[::1\]|http://\[::1\]:*) ;;
    *)
      printf 'Refusing to use non-local %s: %s\n' "$label" "$url" >&2
      printf 'TicketHive load tests must run against the local isolated stack.\n' >&2
      return 2
      ;;
  esac
}
