#!/usr/bin/env bash
# Shared by seed.sh and cleanup.sh (sourced, not run).

# Every show that seed.sh creates belongs to this organizer, so cleanup.sh can
# find load-test rows in the Inventory database without touching anything else.
# Never use this id for real data.
LOAD_TEST_ORGANIZER_ID="10ad7e57-0000-4000-8000-000000000001"
