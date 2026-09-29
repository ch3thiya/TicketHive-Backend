#!/usr/bin/env bash
# TicketHive - Container Apps and migration jobs (Sprint 3, SCRUM-115)
#
#   ./infra/02-container-apps.sh
#
# Creates one Container App and one migration job per service. Apps start on a
# placeholder image; deploy.yml replaces it with the real one on the first
# deploy. Safe to re-run: existing apps are updated, not recreated, and their
# current image is preserved.
#
# Decisions:
#   - Ingress is EXTERNAL in Sprint 3 so each service can be smoke-tested and
#     QA can reach it. Phase 6 switches every backend app to internal once the
#     gateway is the only public entry point.
#   - min-replicas 0 everywhere: pinning five services at one replica costs
#     more than the whole student credit. infra/10-start.sh pins them for a
#     work or demo session.
#   - Secrets are Key Vault references resolved by the apps managed identity,
#     so no secret value is ever passed on a command line or stored in an app.

set -euo pipefail

RG="TicketHive-RG"
ENVIRONMENT="tickethive-env"
ACR="tickethiveacr"
KV="tickethive-kv"
MI="tickethive-apps-mi"

PLACEHOLDER="mcr.microsoft.com/k8se/quickstart:latest"
CPU="0.25"
MEMORY="0.5Gi"
MAX_REPLICAS="2"

SERVICES=(identity catalog inventory waitingroom booking payment notification)

say()  { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
note() { printf '    %s\n' "$1"; }

ACR_SERVER="$(az acr show -g "$RG" -n "$ACR" --query loginServer -o tsv)"
MI_ID="$(az identity show -g "$RG" -n "$MI" --query id -o tsv)"
KV_URI="https://${KV}.vault.azure.net/secrets"
KAFKA="$(az keyvault secret show --vault-name "$KV" -n kafka-bootstrap-servers --query value -o tsv)"

# Asgardeo values are the same for every service and are not secret.
JWT_AUTHORITY="https://api.asgardeo.io/t/orgvx6qo/oauth2/token"
JWT_AUDIENCE="vAVPIdqvfL3f78HtKFBPMLv3Qywa"
WSO2_BASE="https://api.asgardeo.io/t/orgvx6qo/"
WSO2_TOKEN="https://api.asgardeo.io/t/orgvx6qo/oauth2/token"

# ------------------------------------------------------------------ secrets --
# Every app gets the same secret names; unused ones cost nothing.
secret_args() {  # $1 = service
  local s=(
    "connstr=${KV_URI}/connstr-$1"
    "appinsights=${KV_URI}/appinsights-connection-string"
    "wso2-admin-user=${KV_URI}/wso2-admin-username"
    "wso2-admin-pass=${KV_URI}/wso2-admin-password"
    "wso2-m2m-id=${KV_URI}/wso2-m2m-client-id"
    "wso2-m2m-secret=${KV_URI}/wso2-m2m-client-secret"
    "payhere-merchant-id=${KV_URI}/payhere-merchant-id"
    "payhere-merchant-secret=${KV_URI}/payhere-merchant-secret"
    "payhere-app-id=${KV_URI}/payhere-app-id"
    "payhere-app-secret=${KV_URI}/payhere-app-secret"
    "emailjs-service-id=${KV_URI}/emailjs-service-id"
    "emailjs-template-id=${KV_URI}/emailjs-template-id"
    "emailjs-public-key=${KV_URI}/emailjs-public-key"
    "emailjs-private-key=${KV_URI}/emailjs-private-key"
    "admission-private=${KV_URI}/admission-private-key"
    "admission-public=${KV_URI}/admission-public-key"
  )
  local out=()
  for entry in "${s[@]}"; do
    out+=("${entry%%=*}=keyvaultref:${entry#*=},identityref:${MI_ID}")
  done
  printf '%s\n' "${out[@]}"
}

# --------------------------------------------------------------- environment --
common_env() {
  printf '%s\n' \
    "ConnectionStrings__DefaultConnection=secretref:connstr" \
    "APPLICATIONINSIGHTS_CONNECTION_STRING=secretref:appinsights" \
    "ASPNETCORE_URLS=http://+:8080"
}

service_env() {  # $1 = service
  common_env
  case "$1" in
    identity)
      printf '%s\n' \
        "Jwt__Authority=${JWT_AUTHORITY}" "Jwt__Audience=${JWT_AUDIENCE}" \
        "Wso2__BaseUrl=${WSO2_BASE}" \
        "Wso2__AdminUsername=secretref:wso2-admin-user" \
        "Wso2__AdminPassword=secretref:wso2-admin-pass" \
        "Wso2__M2mClientId=secretref:wso2-m2m-id" \
        "Wso2__M2mClientSecret=secretref:wso2-m2m-secret" \
        "Wso2__CustomSchemaUrn=urn:scim:schemas:extension:tickethive:2.0:User" \
        "Wso2__InternalApi__RequiredScope=identity:read"
      ;;
    catalog)
      printf '%s\n' \
        "Jwt__Authority=${JWT_AUTHORITY}" "Jwt__Audience=${JWT_AUDIENCE}" \
        "Wso2__InternalApi__TokenEndpoint=${WSO2_TOKEN}" \
        "Wso2__InternalApi__ClientId=secretref:wso2-m2m-id" \
        "Wso2__InternalApi__ClientSecret=secretref:wso2-m2m-secret" \
        "Wso2__InternalApi__Scope=inventory:write identity:read" \
        "Wso2__InternalApi__RequiredScope=catalog:read"
      ;;
    inventory)
      printf '%s\n' \
        "Jwt__Authority=${JWT_AUTHORITY}" "Jwt__Audience=${JWT_AUDIENCE}" \
        "Wso2__InternalApi__RequiredScope=inventory:write" \
        "HoldExpiry__Sweep__IntervalSeconds=10" \
        "HoldExpiry__Sweep__BatchSize=200" \
        "AdmissionToken__PublicKeyPem=secretref:admission-public"
      ;;
    waitingroom)
      printf '%s\n' \
        "Jwt__Authority=${JWT_AUTHORITY}" "Jwt__Audience=${JWT_AUDIENCE}" \
        "QueueDefaults__AdmitBatch=50" \
        "QueueDefaults__AdmitIntervalSeconds=30" \
        "QueueDefaults__PrequeueWindowMinutes=30" \
        "QueueSellOutCheck__CheckIntervalSeconds=60" \
        "AdmissionToken__Issuer=tickethive-waiting-room" \
        "AdmissionToken__ExpiryMinutes=15" \
        "AdmissionToken__PrivateKeyPem=secretref:admission-private" \
        "Wso2__InternalApi__TokenEndpoint=${WSO2_TOKEN}" \
        "Wso2__InternalApi__ClientId=secretref:wso2-m2m-id" \
        "Wso2__InternalApi__ClientSecret=secretref:wso2-m2m-secret" \
        "Wso2__InternalApi__Scope=catalog:read"
      ;;
    booking)
      printf '%s\n' \
        "Jwt__Authority=${JWT_AUTHORITY}" "Jwt__Audience=${JWT_AUDIENCE}" \
        "Kafka__BootstrapServers=${KAFKA}" \
        "PayHere__MerchantId=secretref:payhere-merchant-id" \
        "PayHere__MerchantSecret=secretref:payhere-merchant-secret"
      ;;
    payment)
      printf '%s\n' \
        "Jwt__Authority=${JWT_AUTHORITY}" "Jwt__Audience=${JWT_AUDIENCE}" \
        "Kafka__BootstrapServers=${KAFKA}" \
        "PayHere__MerchantId=secretref:payhere-merchant-id" \
        "PayHere__MerchantSecret=secretref:payhere-merchant-secret" \
        "PayHere__AppId=secretref:payhere-app-id" \
        "PayHere__AppSecret=secretref:payhere-app-secret"
      ;;
    notification)
      printf '%s\n' \
        "Kafka__BootstrapServers=${KAFKA}" \
        "Kafka__GroupId=notification-service-group-v2" \
        "EmailJS__ApiUrl=https://api.emailjs.com/api/v1.0/email/send" \
        "EmailJS__ServiceId=secretref:emailjs-service-id" \
        "EmailJS__TemplateId=secretref:emailjs-template-id" \
        "EmailJS__PublicKey=secretref:emailjs-public-key" \
        "EmailJS__PrivateKey=secretref:emailjs-private-key"
      ;;
  esac
}

# --------------------------------------------------------------------- apps --
say "Container Apps"
for svc in "${SERVICES[@]}"; do
  app="tickethive-${svc}"
  mapfile -t SECRETS < <(secret_args "$svc")
  mapfile -t ENVVARS < <(service_env "$svc")

  if az containerapp show -g "$RG" -n "$app" -o none 2>/dev/null; then
    az containerapp secret set -g "$RG" -n "$app" --secrets "${SECRETS[@]}" -o none
    az containerapp update -g "$RG" -n "$app" \
      --set-env-vars "${ENVVARS[@]}" \
      --cpu "$CPU" --memory "$MEMORY" \
      --min-replicas 0 --max-replicas "$MAX_REPLICAS" -o none
    note "updated: $app"
  else
    az containerapp create -g "$RG" -n "$app" \
      --environment "$ENVIRONMENT" \
      --image "$PLACEHOLDER" \
      --user-assigned "$MI_ID" \
      --registry-server "$ACR_SERVER" --registry-identity "$MI_ID" \
      --secrets "${SECRETS[@]}" \
      --env-vars "${ENVVARS[@]}" \
      --ingress external --target-port 8080 \
      --cpu "$CPU" --memory "$MEMORY" \
      --min-replicas 0 --max-replicas "$MAX_REPLICAS" -o none
    note "created: $app"
  fi
done

# ----------------------------------------------------- service-to-service URLs --
# Done after creation, because the FQDNs only exist once the apps do.
say "Service addresses"
declare -A URL
for svc in "${SERVICES[@]}"; do
  URL[$svc]="https://$(az containerapp show -g "$RG" -n "tickethive-${svc}" \
    --query properties.configuration.ingress.fqdn -o tsv)"
  note "${svc}: ${URL[$svc]}"
done

az containerapp update -g "$RG" -n tickethive-catalog --set-env-vars \
  "Services__Identity__BaseUrl=${URL[identity]}" \
  "Services__Inventory__BaseUrl=${URL[inventory]}" -o none

az containerapp update -g "$RG" -n tickethive-waitingroom --set-env-vars \
  "Services__Catalog__BaseUrl=${URL[catalog]}" \
  "Services__Inventory__BaseUrl=${URL[inventory]}" -o none

# Booking uses its own key name for this; see the note in the Phase 5 handover.
az containerapp update -g "$RG" -n tickethive-booking --set-env-vars \
  "InventoryService__BaseUrl=${URL[inventory]}" \
  "Services__Inventory__BaseUrl=${URL[inventory]}" \
  "Services__Payment__BaseUrl=${URL[payment]}" -o none

# --------------------------------------------------------- migration jobs --
say "Migration jobs"
for svc in "${SERVICES[@]}"; do
  job="tickethive-${svc}-migrate"
  mapfile -t SECRETS < <(secret_args "$svc")

  if az containerapp job show -g "$RG" -n "$job" -o none 2>/dev/null; then
    note "exists: $job"
  else
    az containerapp job create -g "$RG" -n "$job" \
      --environment "$ENVIRONMENT" \
      --trigger-type Manual \
      --replica-timeout 600 --replica-retry-limit 1 \
      --image "$PLACEHOLDER" \
      --user-assigned "$MI_ID" \
      --registry-server "$ACR_SERVER" --registry-identity "$MI_ID" \
      --secrets "${SECRETS[@]}" \
      --env-vars "ConnectionStrings__DefaultConnection=secretref:connstr" \
      --args="--migrate" \
      --cpu "$CPU" --memory "$MEMORY" -o none
    note "created: $job"
  fi
done

say "Done"
cat <<SUMMARY

Seven apps and seven migration jobs exist, all on a placeholder image and
scaled to zero. The first deploy from 'dev' replaces the images.

Public addresses (Sprint 3 only - Phase 6 makes these internal):

SUMMARY
for svc in "${SERVICES[@]}"; do printf '  %-14s %s\n' "$svc" "${URL[$svc]}"; done
cat <<'SUMMARY'

Not set yet, and needed before the checkout path works end to end:
  - Cors__AllowedOrigins on every service (Phase 6, once the frontend is up)
  - PayHere__ReturnUrl / CancelUrl / NotifyUrl on Booking and Payment
SUMMARY