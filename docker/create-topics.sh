#!/bin/bash
# Creates the Kafka topics the services use, plus a dead-letter topic for each.
# Idempotent: --if-not-exists, so it is safe to run on every `up`.
set -e

BOOTSTRAP=kafka:29092
PARTITIONS=3
REPLICATION=1

TOPICS="tickethive.payment.succeeded \
tickethive.payment.failed \
tickethive.order.confirmed \
tickethive.tickets.issued"

for topic in $TOPICS; do
  for name in "$topic" "$topic.dlq"; do
    kafka-topics --bootstrap-server "$BOOTSTRAP" \
      --create --if-not-exists \
      --topic "$name" \
      --partitions "$PARTITIONS" \
      --replication-factor "$REPLICATION"
    echo "ready: $name"
  done
done

echo "--- topics on the broker ---"
kafka-topics --bootstrap-server "$BOOTSTRAP" --list