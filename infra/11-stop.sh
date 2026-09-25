#!/usr/bin/env bash
# TicketHive — stop everything that bills by the second.
#
#   ./infra/11-stop.sh
#
# Leaves running: the registry, Postgres storage, the public IP and the OS
# disk. Those are flat monthly charges and cannot be paused.
#
# Note: Azure restarts a stopped Postgres server automatically after 7 days.

set -euo pipefail

RG="TicketHive-RG"
PG="tickethive-pg"
VM="tickethive-kafka"

APPS=(tickethive-identity tickethive-catalog tickethive-inventory \
      tickethive-waitingroom tickethive-booking tickethive-payment \
      tickethive-notification tickethive-gateway)

say()  { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
note() { printf '    %s\n' "$1"; }

say "Services to zero"
for app in "${APPS[@]}"; do
  if az containerapp show -g "$RG" -n "$app" -o none 2>/dev/null; then
    az containerapp update -g "$RG" -n "$app" --min-replicas 0 -o none
    note "scaled to zero: $app"
  fi
done

say "Kafka broker"
vm_state="$(az vm get-instance-view -g "$RG" -n "$VM" --query "instanceView.statuses[?starts_with(code,'PowerState/')].code" -o tsv 2>/dev/null || echo "")"
if [[ "$vm_state" == "PowerState/deallocated" ]]; then
  note "already deallocated"
else
  # deallocate, not just stop: a stopped-but-allocated VM still bills.
  az vm deallocate -g "$RG" -n "$VM" -o none
  note "deallocated"
fi

say "PostgreSQL"
state="$(az postgres flexible-server show -g "$RG" -n "$PG" --query state -o tsv)"
if [[ "$state" == "Stopped" ]]; then
  note "already stopped"
else
  az postgres flexible-server stop -g "$RG" -n "$PG" -o none
  note "stopped"
fi

say "Stopped"
note "still billing monthly: registry, database storage, public IP, OS disk"
