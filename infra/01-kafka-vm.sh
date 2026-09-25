#!/usr/bin/env bash
# TicketHive - self-hosted Kafka broker (Sprint 3, SCRUM-113)
#
#   ./infra/01-kafka-vm.sh
#
# Creates a B1s Ubuntu VM in the Kafka subnet running Apache Kafka in KRaft
# mode with Kafka UI, both in Docker, with the log directory on the OS disk so
# messages survive a broker restart and a VM deallocate.
#
# Sizing and access (chosen against an Azure for Students credit):
#   - B1s (1 GB). Kafka heap is capped at 512 MB and a 2 GB swap file absorbs
#     the burst when Kafka UI starts. Kafka UI is installed but NOT started -
#     start it for a demo, stop it afterwards (see 10-start.sh).
#   - Broker port 9092 is reachable ONLY from the Container Apps subnet.
#   - SSH is reachable only from the address this script runs from.
#   - Kafka UI binds to localhost on the VM; reach it through an SSH tunnel.
#
# Re-runnable: skips whatever already exists.

set -euo pipefail

LOCATION="southeastasia"
RG="TicketHive-RG"
VNET="tickethive-vnet"
SNET_KAFKA="snet-kafka"
SNET_APPS_PREFIX="10.20.0.0/23"

VM="tickethive-kafka"
VM_SIZE="Standard_B2ts_v2"
VM_IMAGE="Ubuntu2204"
VM_USER="azureuser"
NSG="tickethive-kafka-nsg"

KAFKA_IMAGE="confluentinc/cp-kafka:7.6.0"
KAFKA_UI_IMAGE="provectuslabs/kafka-ui:latest"
KAFKA_HEAP="-Xmx512M -Xms256M"
KV="tickethive-kv"

TOPICS=(tickethive.payment.succeeded tickethive.payment.failed \
        tickethive.order.confirmed tickethive.tickets.issued)

say()  { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
note() { printf '    %s\n' "$1"; }

MY_IP="$(curl -fsS https://api.ipify.org)"
SNET_KAFKA_ID="$(az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_KAFKA" --query id -o tsv)"

# ---------------------------------------------------------- network rules --
say "Network security group"
if ! az network nsg show -g "$RG" -n "$NSG" -o none 2>/dev/null; then
  az network nsg create -g "$RG" -n "$NSG" -l "$LOCATION" -o none
fi

rule() {  # name, priority, port, source
  az network nsg rule create -g "$RG" --nsg-name "$NSG" -n "$1" \
    --priority "$2" --access Allow --protocol Tcp --direction Inbound \
    --source-address-prefixes "$4" --destination-port-ranges "$3" -o none 2>/dev/null \
    && note "rule: $1 ($3 from $4)" || note "rule exists: $1"
}
rule allow-kafka-from-apps 100 9092 "$SNET_APPS_PREFIX"
rule allow-ssh-from-devops 200 22   "$MY_IP"

az network vnet subnet update -g "$RG" --vnet-name "$VNET" -n "$SNET_KAFKA" \
  --network-security-group "$NSG" -o none

# ------------------------------------------------------------- cloud-init --
# Runs once on first boot: swap, Docker, Kafka (KRaft), Kafka UI (not started),
# and the topics. Written to a temp file so the YAML stays readable here.
CLOUD_INIT="$(mktemp)"
trap 'rm -f "$CLOUD_INIT"' EXIT

cat > "$CLOUD_INIT" <<CLOUDINIT
#cloud-config
package_update: true
packages:
  - ca-certificates
  - curl

write_files:
  - path: /opt/kafka/docker-compose.yml
    permissions: '0644'
    content: |
      services:
        kafka:
          image: ${KAFKA_IMAGE}
          container_name: kafka
          restart: always
          ports:
            - "9092:9092"
          environment:
            CLUSTER_ID: MkU3OEVCNTcwNTFENDM2Qk
            KAFKA_NODE_ID: 1
            KAFKA_PROCESS_ROLES: broker,controller
            KAFKA_CONTROLLER_QUORUM_VOTERS: 1@localhost:29093
            KAFKA_LISTENERS: PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:29093
            KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://ADVERTISED_IP:9092
            KAFKA_LISTENER_SECURITY_PROTOCOL_MAP: PLAINTEXT:PLAINTEXT,CONTROLLER:PLAINTEXT
            KAFKA_INTER_BROKER_LISTENER_NAME: PLAINTEXT
            KAFKA_CONTROLLER_LISTENER_NAMES: CONTROLLER
            KAFKA_LOG_DIRS: /var/lib/kafka/data
            KAFKA_AUTO_CREATE_TOPICS_ENABLE: "false"
            KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR: 1
            KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR: 1
            KAFKA_TRANSACTION_STATE_LOG_MIN_ISR: 1
            KAFKA_LOG_RETENTION_HOURS: 168
            KAFKA_HEAP_OPTS: "${KAFKA_HEAP}"
          volumes:
            - /var/lib/kafka/data:/var/lib/kafka/data

        # Installed but not started. Start it only when you need to look at
        # topics, then stop it again - B1s has no room for two JVMs at rest.
        kafka-ui:
          image: ${KAFKA_UI_IMAGE}
          container_name: kafka-ui
          restart: "no"
          profiles: ["ui"]
          ports:
            - "127.0.0.1:8085:8080"
          environment:
            KAFKA_CLUSTERS_0_NAME: azure
            KAFKA_CLUSTERS_0_BOOTSTRAPSERVERS: kafka:9092
            JAVA_OPTS: "-Xmx256M"
          network_mode: "service:kafka"

  - path: /opt/kafka/create-topics.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      set -e
      for t in ${TOPICS[@]}; do
        for name in "\$t" "\$t.dlq"; do
          docker exec kafka kafka-topics --bootstrap-server localhost:9092 \\
            --create --if-not-exists --topic "\$name" \\
            --partitions 3 --replication-factor 1
        done
      done
      docker exec kafka kafka-topics --bootstrap-server localhost:9092 --list

runcmd:
  # 2 GB swap: lets Kafka UI start on a 1 GB machine without disturbing Kafka.
  - fallocate -l 2G /swapfile
  - chmod 600 /swapfile
  - mkswap /swapfile
  - swapon /swapfile
  - echo '/swapfile none swap sw 0 0' >> /etc/fstab
  - sysctl -w vm.swappiness=10
  - echo 'vm.swappiness=10' >> /etc/sysctl.conf

  - curl -fsSL https://get.docker.com | sh
  - usermod -aG docker ${VM_USER}
  - mkdir -p /var/lib/kafka/data
  - chown -R 1000:1000 /var/lib/kafka/data
  - systemctl enable docker

  - sed -i "s|ADVERTISED_IP|\$(hostname -I | awk '{print \$1}')|" /opt/kafka/docker-compose.yml
  - cd /opt/kafka && docker compose up -d kafka
  - sleep 45
  - /opt/kafka/create-topics.sh || true
  - touch /opt/kafka/.ready
CLOUDINIT

# -------------------------------------------------------------------- vm --
say "Virtual machine"
if az vm show -g "$RG" -n "$VM" -o none 2>/dev/null; then
  note "exists - leaving it alone"
else
  az vm create -g "$RG" -n "$VM" -l "$LOCATION" \
    --image "$VM_IMAGE" \
    --size "$VM_SIZE" \
    --admin-username "$VM_USER" \
    --generate-ssh-keys \
    --subnet "$SNET_KAFKA_ID" \
    --nsg "" \
    --public-ip-sku Standard \
    --storage-sku StandardSSD_LRS \
    --os-disk-size-gb 30 \
    --custom-data "$CLOUD_INIT" \
    -o none
  note "created - cloud-init takes about 3 minutes to finish"
fi

PRIVATE_IP="$(az vm show -g "$RG" -n "$VM" -d --query privateIps -o tsv)"
PUBLIC_IP="$(az vm show -g "$RG" -n "$VM" -d --query publicIps -o tsv)"

az keyvault secret set --vault-name "$KV" -n kafka-bootstrap-servers \
  --value "${PRIVATE_IP}:9092" -o none

# ----------------------------------------------------------------- report --
say "Done"
cat <<SUMMARY

  broker (from the services)  ${PRIVATE_IP}:9092
  ssh                         ssh ${VM_USER}@${PUBLIC_IP}
  stored in Key Vault as      kafka-bootstrap-servers

Wait for cloud-init, then check the broker and its topics:

  az vm run-command invoke -g ${RG} -n ${VM} --command-id RunShellScript \\
    --scripts "cat /opt/kafka/.ready && docker ps --format '{{.Names}}: {{.Status}}' && docker exec kafka kafka-topics --bootstrap-server localhost:9092 --list" \\
    --query "value[0].message" -o tsv

Kafka UI, only while you need it:

  az vm run-command invoke -g ${RG} -n ${VM} --command-id RunShellScript \\
    --scripts "cd /opt/kafka && docker compose --profile ui up -d kafka-ui"
  ssh -L 8085:localhost:8085 ${VM_USER}@${PUBLIC_IP}      # then open localhost:8085
  az vm run-command invoke -g ${RG} -n ${VM} --command-id RunShellScript \\
    --scripts "docker stop kafka-ui"

Stop it when you are not using it - infra/11-stop.sh does this for everything.

SUMMARY
