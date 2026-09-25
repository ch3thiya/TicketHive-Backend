#!/usr/bin/env bash
# TicketHive — start the billable environment for a work or demo session.
#
#   ./infra/10-start.sh              # database, broker, services
#   ./infra/10-start.sh --with-ui    # also start Kafka UI on the broker
#
# Run infra/11-stop.sh when you finish. Everything started here bills by the
# second; left running for a month it costs more than the student credit holds.

set -euo pipefail

RG="TicketHive-RG"
PG="tickethive-pg"
VM="tickethive-kafka"

# Services that must actually be running to behave correctly: the hold expiry
# sweeper, the queue admission scheduler and the Kafka consumers.
ALWAYS_ON=(tickethive-inventory tickethive-waitingroom tickethive-booking \
           tickethive-payment tickethive-notification)

WITH_UI=false
[[ "${1:-}" == "--with-ui" ]] && WITH_UI=true

say()  { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
note() { printf '    %s\n' "$1"; }

say "PostgreSQL"
state="$(az postgres flexible-server show -g "$RG" -n "$PG" --query state -o tsv)"
if [[ "$state" == "Ready" ]]; then
  note "already running"
else
  az postgres flexible-server start -g "$RG" -n "$PG" -o none
  note "started"
fi

say "Kafka broker"
vm_state="$(az vm get-instance-view -g "$RG" -n "$VM" --query "instanceView.statuses[?starts_with(code,'PowerState/')].code" -o tsv 2>/dev/null || echo "")"
if [[ "$vm_state" == "PowerState/running" ]]; then
  note "already running"
else
  az vm start -g "$RG" -n "$VM" -o none
  note "started"
fi

if $WITH_UI; then
  note "starting Kafka UI"
  az vm run-command invoke -g "$RG" -n "$VM" --command-id RunShellScript \
    --scripts "cd /opt/kafka && docker compose --profile ui up -d kafka-ui" -o none
  PUBLIC_IP="$(az vm show -g "$RG" -n "$VM" -d --query publicIps -o tsv)"
  note "tunnel: ssh -L 8085:localhost:8085 azureuser@${PUBLIC_IP}  then open localhost:8085"
fi

say "Services"
for app in "${ALWAYS_ON[@]}"; do
  if az containerapp show -g "$RG" -n "$app" -o none 2>/dev/null; then
    az containerapp update -g "$RG" -n "$app" --min-replicas 1 -o none
    note "pinned: $app"
  else
    note "not deployed yet: $app"
  fi
done

say "Ready"
note "remember: ./infra/11-stop.sh when you finish"
