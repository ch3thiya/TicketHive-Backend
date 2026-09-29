#!/usr/bin/env bash
# TicketHive — Azure base environment (Sprint 3, SCRUM-115)
#
# Builds the whole environment into an empty resource group, sized for an
# Azure for Students credit. Safe to re-run: every step is create-if-missing.
#
#   ./infra/00-infrastructure.sh
#
# Requires: az CLI, logged in, Owner on the subscription.
#
# Cost notes (the reason for several choices below):
#   - Consumption-only Container Apps environment: no baseline charge.
#   - One Postgres B1MS with a database per service, not one server per service.
#   - 32 GB storage is the Burstable-tier minimum; it can grow but never shrink.
#   - Registry admin user off: images are pulled with a managed identity, so
#     there is no registry password to store, rotate or leak.
#   - App registrations are NOT used (the tenant blocks them for students) —
#     GitHub signs in through a managed identity with federated credentials.
#
# Everything time-billed (Postgres compute, the Kafka VM, app replicas) is
# started and stopped by infra/10-start.sh and infra/11-stop.sh.

set -euo pipefail

# ---------------------------------------------------------------- settings --
LOCATION="southeastasia"
RG="TicketHive-RG"

VNET="tickethive-vnet"
SNET_APPS="snet-apps"
SNET_KAFKA="snet-kafka"

ACR="tickethiveacr"                 # globally unique
KV="tickethive-kv"                  # globally unique
LAW="tickethive-logs"
APPI="tickethive-insights"
ENVIRONMENT="tickethive-env"

PG="tickethive-pg"                  # globally unique
PG_ADMIN="tickethiveadmin"
PG_STORAGE_GB=32                    # Burstable minimum
PG_BACKUP_DAYS=7                    # minimum

MI_APPS="tickethive-apps-mi"        # apps: pull images, read secrets
MI_GITHUB="tickethive-github-mi"    # GitHub Actions: build and deploy

BACKEND_REPO="ch3thiya/TicketHive-Backend"
FRONTEND_REPO="ch3thiya/TicketHive-Frontend"
# Immutable subjects (owner@ownerId/repo@repoId) that GitHub issues for new repos
BACKEND_REPO_IMMUTABLE="ch3thiya@121000739/TicketHive-Backend@1340388630"
FRONTEND_REPO_IMMUTABLE="ch3thiya@121000739/TicketHive-Frontend@1340388564"
DEPLOY_BRANCH="dev"
GH_ENVIRONMENT="azure-dev"

BUDGET_TOTAL=40                     # USD over the budget period; alerts at 50/80/100%

SERVICES=(identity catalog inventory waitingroom booking payment notification)

say()  { printf '\n\033[1m==> %s\033[0m\n' "$1"; }
note() { printf '    %s\n' "$1"; }

SUB_ID="$(az account show --query id -o tsv)"
TENANT_ID="$(az account show --query tenantId -o tsv)"
MY_ID="$(az ad signed-in-user show --query id -o tsv)"
MY_IP="$(curl -fsS https://api.ipify.org || true)"

# ----------------------------------------------------------- resource group --
say "Resource group"
az group create -n "$RG" -l "$LOCATION" -o none

# ----------------------------------------------------------------- network --
# The Container Apps environment must be created inside this network. That is
# what lets the services reach Kafka on a private address in Phase 4, and it
# cannot be added to an existing environment.
say "Virtual network and subnets"
az network vnet create -g "$RG" -n "$VNET" \
  --address-prefixes 10.20.0.0/16 \
  --subnet-name "$SNET_APPS" --subnet-prefixes 10.20.0.0/23 -o none

az network vnet subnet update -g "$RG" --vnet-name "$VNET" -n "$SNET_APPS" \
  --delegations Microsoft.App/environments -o none

az network vnet subnet create -g "$RG" --vnet-name "$VNET" -n "$SNET_KAFKA" \
  --address-prefixes 10.20.4.0/24 -o none

SNET_APPS_ID="$(az network vnet subnet show -g "$RG" --vnet-name "$VNET" -n "$SNET_APPS" --query id -o tsv)"

# --------------------------------------------------------------- monitoring --
say "Log Analytics and Application Insights"
az monitor log-analytics workspace create -g "$RG" -n "$LAW" -l "$LOCATION" \
  --retention-time 30 -o none

LAW_ID="$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query id -o tsv)"
LAW_CUSTOMER_ID="$(az monitor log-analytics workspace show -g "$RG" -n "$LAW" --query customerId -o tsv)"
LAW_KEY="$(az monitor log-analytics workspace get-shared-keys -g "$RG" -n "$LAW" --query primarySharedKey -o tsv)"

# Created through the generic resource command so the run does not depend on
# the application-insights CLI extension being installable.
if ! az resource show -g "$RG" -n "$APPI" --resource-type Microsoft.Insights/components -o none 2>/dev/null; then
  az resource create -g "$RG" -n "$APPI" \
    --resource-type Microsoft.Insights/components \
    --location "$LOCATION" \
    --properties "{\"Application_Type\":\"web\",\"WorkspaceResourceId\":\"${LAW_ID}\",\"IngestionMode\":\"LogAnalytics\"}" -o none
fi

APPI_CONNECTION="$(az resource show -g "$RG" -n "$APPI" \
  --resource-type Microsoft.Insights/components \
  --query properties.ConnectionString -o tsv)"

note "set a daily ingestion cap in the portal: Application Insights > Usage and estimated costs"

# ----------------------------------------------------------------- registry --
say "Container registry"
az acr create -g "$RG" -n "$ACR" --sku Basic --admin-enabled false -o none
ACR_ID="$(az acr show -g "$RG" -n "$ACR" --query id -o tsv)"
ACR_SERVER="$(az acr show -g "$RG" -n "$ACR" --query loginServer -o tsv)"

# --------------------------------------------------------------- identities --
say "Managed identities"
az identity create -g "$RG" -n "$MI_APPS" -l "$LOCATION" -o none
MI_APPS_ID="$(az identity show -g "$RG" -n "$MI_APPS" --query id -o tsv)"
MI_APPS_PRINCIPAL="$(az identity show -g "$RG" -n "$MI_APPS" --query principalId -o tsv)"
MI_APPS_CLIENT="$(az identity show -g "$RG" -n "$MI_APPS" --query clientId -o tsv)"

az identity create -g "$RG" -n "$MI_GITHUB" -l "$LOCATION" -o none
MI_GITHUB_PRINCIPAL="$(az identity show -g "$RG" -n "$MI_GITHUB" --query principalId -o tsv)"
MI_GITHUB_CLIENT="$(az identity show -g "$RG" -n "$MI_GITHUB" --query clientId -o tsv)"

say "GitHub federated credentials"
add_federated() {  # $1 = credential name, $2 = subject
  if az identity federated-credential show --name "$1" --identity-name "$MI_GITHUB" -g "$RG" -o none 2>/dev/null; then
    note "exists:  $1"
  else
    az identity federated-credential create \
      --name "$1" --identity-name "$MI_GITHUB" -g "$RG" \
      --issuer https://token.actions.githubusercontent.com \
      --subject "$2" \
      --audiences api://AzureADTokenExchange -o none
    note "created: $1"
  fi
}
add_federated backend-branch  "repo:${BACKEND_REPO}:ref:refs/heads/${DEPLOY_BRANCH}"
add_federated backend-env     "repo:${BACKEND_REPO}:environment:${GH_ENVIRONMENT}"
add_federated frontend-branch "repo:${FRONTEND_REPO}:ref:refs/heads/${DEPLOY_BRANCH}"
add_federated frontend-env    "repo:${FRONTEND_REPO}:environment:${GH_ENVIRONMENT}"
add_federated backend-dev-immutable  "repo:${BACKEND_REPO_IMMUTABLE}:ref:refs/heads/dev"
add_federated backend-main           "repo:${BACKEND_REPO}:ref:refs/heads/main"
add_federated backend-main-immutable "repo:${BACKEND_REPO_IMMUTABLE}:ref:refs/heads/main"
add_federated frontend-dev-immutable "repo:${FRONTEND_REPO_IMMUTABLE}:ref:refs/heads/dev"

say "Role assignments"
assign() {  # $1 = principal id, $2 = role, $3 = scope
  az role assignment create --assignee-object-id "$1" \
    --assignee-principal-type ServicePrincipal \
    --role "$2" --scope "$3" -o none 2>/dev/null && note "granted: $2" || note "already: $2"
}
assign "$MI_APPS_PRINCIPAL"   "AcrPull"     "$ACR_ID"
assign "$MI_GITHUB_PRINCIPAL" "Contributor" "/subscriptions/${SUB_ID}/resourceGroups/${RG}"
assign "$MI_GITHUB_PRINCIPAL" "AcrPush"     "$ACR_ID"

# ---------------------------------------------------------------- key vault --
say "Key Vault"
az keyvault create -g "$RG" -n "$KV" -l "$LOCATION" \
  --enable-rbac-authorization true \
  --retention-days 7 -o none
KV_ID="$(az keyvault show -g "$RG" -n "$KV" --query id -o tsv)"

az role assignment create --assignee-object-id "$MY_ID" --assignee-principal-type User \
  --role "Key Vault Secrets Officer" --scope "$KV_ID" -o none 2>/dev/null || true
assign "$MI_APPS_PRINCIPAL" "Key Vault Secrets User" "$KV_ID"

note "waiting for the role assignment to take effect"
sleep 30

# ----------------------------------------------------------------- postgres --
# One server, one database per service. The old environment ran two separate
# servers, which meant paying for two lots of compute and storage.
say "PostgreSQL flexible server"

if az postgres flexible-server show -g "$RG" -n "$PG" -o none 2>/dev/null; then
  note "server exists — leaving it and its password alone"
  PG_PASSWORD=""
else
  PG_PASSWORD="$(openssl rand -base64 30 | tr -dc 'A-Za-z0-9' | cut -c1-28)"
  az postgres flexible-server create -g "$RG" -n "$PG" -l "$LOCATION" \
    --tier Burstable --sku-name Standard_B1ms \
    --version 16 \
    --storage-size "$PG_STORAGE_GB" \
    --backup-retention "$PG_BACKUP_DAYS" \
    --admin-user "$PG_ADMIN" --admin-password "$PG_PASSWORD" \
    --public-access 0.0.0.0 \
    --yes -o none
  az keyvault secret set --vault-name "$KV" -n postgres-admin-password --value "$PG_PASSWORD" -o none
fi

PG_FQDN="$(az postgres flexible-server show -g "$RG" -n "$PG" --query fullyQualifiedDomainName -o tsv)"

if [[ -n "$MY_IP" ]]; then
  az postgres flexible-server firewall-rule create -g "$RG" -n "$PG" \
    --rule-name "allow-devops-ip" \
    --start-ip-address "$MY_IP" --end-ip-address "$MY_IP" -o none 2>/dev/null || true
  note "firewall open to $MY_IP (teammates add their own rules)"
fi

if [[ -z "$PG_PASSWORD" ]]; then
  PG_PASSWORD="$(az keyvault secret show --vault-name "$KV" -n postgres-admin-password --query value -o tsv)"
fi

for svc in "${SERVICES[@]}"; do
  db="tickethive_${svc}"
  az postgres flexible-server db create -g "$RG" --server-name "$PG" --name "$db" -o none || note "could not create $db"
  az keyvault secret set --vault-name "$KV" -n "connstr-${svc}" \
    --value "Host=${PG_FQDN};Database=${db};Username=${PG_ADMIN};Password=${PG_PASSWORD};SSL Mode=Require;Trust Server Certificate=true;Maximum Pool Size=10" -o none
  note "database and connection string: $db"
done

az keyvault secret set --vault-name "$KV" -n appinsights-connection-string \
  --value "$APPI_CONNECTION" -o none

# --------------------------------------------------- container apps environment --
# Consumption only: no workload profiles, so there is no baseline charge and
# every app bills per second of actual use, with a monthly free grant.
say "Container Apps environment"
if az containerapp env show -g "$RG" -n "$ENVIRONMENT" -o none 2>/dev/null; then
  note "exists"
else
  az containerapp env create -g "$RG" -n "$ENVIRONMENT" -l "$LOCATION" \
    --infrastructure-subnet-resource-id "$SNET_APPS_ID" \
    --logs-destination log-analytics \
    --logs-workspace-id "$LAW_CUSTOMER_ID" \
    --logs-workspace-key "$LAW_KEY" -o none
fi

# ------------------------------------------------------------------- budget --
say "Budget alert"
BUDGET_START="$(date -u +%Y-%m-01)"
BUDGET_END="$(date -u -d "$BUDGET_START +12 months" +%Y-%m-01 2>/dev/null || echo "2027-09-01")"
MY_EMAIL="$(az ad signed-in-user show --query mail -o tsv 2>/dev/null || true)"
[[ -z "$MY_EMAIL" || "$MY_EMAIL" == "null" ]] && MY_EMAIL="$(az ad signed-in-user show --query userPrincipalName -o tsv)"

az consumption budget create \
  --budget-name tickethive-monthly \
  --amount "$BUDGET_TOTAL" \
  --category cost \
  --time-grain Monthly \
  --start-date "$BUDGET_START" \
  --end-date "$BUDGET_END" \
  --resource-group "$RG" \
  --email-contact "$MY_EMAIL" \
  --threshold 50 80 100 -o none 2>/dev/null \
  && note "budget set: USD ${BUDGET_TOTAL}/month, alerts at 50/80/100%" \
  || note "budget not set — add it in the portal under Cost Management > Budgets"

# ------------------------------------------------------------------ summary --
say "Done"
cat <<SUMMARY

GitHub repository variables (Settings > Secrets and variables > Actions > Variables),
in BOTH repos:

  AZURE_CLIENT_ID          ${MI_GITHUB_CLIENT}
  AZURE_TENANT_ID          ${TENANT_ID}
  AZURE_SUBSCRIPTION_ID    ${SUB_ID}
  AZURE_RESOURCE_GROUP     ${RG}
  ACR_LOGIN_SERVER         ${ACR_SERVER}
  CONTAINERAPPS_ENV        ${ENVIRONMENT}

Also create an environment named exactly '${GH_ENVIRONMENT}' in both repos,
restricted to the '${DEPLOY_BRANCH}' branch, or the deploy job cannot get a token.

For Phases 4 and 5:

  apps identity (resource id) ${MI_APPS_ID}
  apps identity (client id)   ${MI_APPS_CLIENT}
  postgres host               ${PG_FQDN}
  kafka subnet                ${SNET_KAFKA} (10.20.4.0/24)
  key vault                   ${KV}

Secrets still to add, from your .env — values never go in the repo:

  az keyvault secret set --vault-name ${KV} -n wso2-admin-username     --value '...'
  az keyvault secret set --vault-name ${KV} -n wso2-admin-password     --value '...'
  az keyvault secret set --vault-name ${KV} -n wso2-m2m-client-id      --value '...'
  az keyvault secret set --vault-name ${KV} -n wso2-m2m-client-secret  --value '...'
  az keyvault secret set --vault-name ${KV} -n wso2-internal-client-id     --value '...'
  az keyvault secret set --vault-name ${KV} -n wso2-internal-client-secret --value '...'
  az keyvault secret set --vault-name ${KV} -n payhere-merchant-id     --value '...'
  az keyvault secret set --vault-name ${KV} -n payhere-merchant-secret --value '...'
  az keyvault secret set --vault-name ${KV} -n payhere-app-id          --value '...'
  az keyvault secret set --vault-name ${KV} -n payhere-app-secret      --value '...'
  az keyvault secret set --vault-name ${KV} -n emailjs-service-id      --value '...'
  az keyvault secret set --vault-name ${KV} -n emailjs-template-id     --value '...'
  az keyvault secret set --vault-name ${KV} -n emailjs-public-key      --value '...'
  az keyvault secret set --vault-name ${KV} -n emailjs-private-key     --value '...'
  az keyvault secret set --vault-name ${KV} -n admission-private-key   --file .keys/admission-private.pem
  az keyvault secret set --vault-name ${KV} -n admission-public-key    --file .keys/admission-public.pem

Nothing time-billed is running yet: Postgres is the only meter until Phase 4
adds the Kafka VM and Phase 5 adds the apps.

SUMMARY