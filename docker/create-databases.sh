#!/bin/sh
# Creates one database per service. Idempotent: safe to run on every `up`,
# including against a volume that already has some of them.
set -e

DATABASES="tickethive_identity \
tickethive_catalog \
tickethive_inventory \
tickethive_waitingroom \
tickethive_booking \
tickethive_payment \
tickethive_notification"

for db in $DATABASES; do
  if psql -h postgres -U postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$db'" | grep -q 1; then
    echo "exists:  $db"
  else
    psql -v ON_ERROR_STOP=1 -h postgres -U postgres -c "CREATE DATABASE $db"
    echo "created: $db"
  fi
done

echo "databases ready"