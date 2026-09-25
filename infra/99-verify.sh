#!/usr/bin/env bash
# TicketHive - verify the DevOps work so far (Phases 1 to 4).
#
#   ./infra/99-verify.sh
#
# Read-only: starts nothing, changes nothing, costs nothing. Azure checks
# confirm resources EXIST; they do not require them to be running.

RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; BOLD=$'\033[1m'; OFF=$'\033[0m'
PASS=0; FAIL=0; WARN=0

ok()   { printf "  ${GREEN}pass${OFF}  %s\n" "$1"; PASS=$((PASS+1)); }
bad()  { printf "  ${RED}FAIL${OFF}  %s\n" "$1"; FAIL=$((FAIL+1)); }
warn() { printf "  ${YELLOW}warn${OFF}  %s\n" "$1"; WARN=$((WARN+1)); }
head() { printf "\n${BOLD}%s${OFF}\n" "$1"; }

check() {  # description, command...
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then ok "$desc"; else bad "$desc"; fi
}

RG="TicketHive-RG"
KV="tickethive-kv"

# ----------------------------------------------------------------- phase 1 --
head "Phase 1 - secrets out of the repository"

if git -C . grep -qI -E '"(MerchantSecret|AppSecret|M2mClientSecret|AdminPassword|PrivateKey|PrivateKeyPem)"[[:space:]]*:[[:space:]]*"[^"]+"' -- '*.json' 2>/dev/null; then
  bad "a non-empty secret value is still committed in a .json file"
else
  ok "no non-empty secret values in committed .json files"
fi

check ".gitleaks.toml present"                 test -f .gitleaks.toml
check ".env is ignored by git"                 git check-ignore -q .env
check ".keys/ is ignored by git"               git check-ignore -q .keys/admission-private.pem
check "no stray psql output file at the root"  bash -c '! ls "tgres psql"* >/dev/null 2>&1'
check "Notification is in the solution"        grep -q "Notification.Service.csproj" TicketHive.slnx
check "no deploy workflows left behind"        bash -c '! ls .github/workflows/*AutoDeployTrigger* >/dev/null 2>&1'

if command -v dotnet >/dev/null && dotnet build TicketHive.slnx -v q --nologo >/dev/null 2>&1; then
  ok "solution builds (all seven services)"
else
  warn "solution did not build - run 'dotnet build TicketHive.slnx' to see why"
fi

# ----------------------------------------------------------------- phase 2 --
head "Phase 2 - local environment"

check "compose file parses"        docker compose config
check "create-databases.sh exists" test -x docker/create-databases.sh
check "create-topics.sh exists"    test -x docker/create-topics.sh
check "old init-db.sql removed"    bash -c '! test -f docker/init-db.sql'
check ".env.example committed"     git ls-files --error-unmatch .env.example

for svc in Booking Payment Notification Catalog Identity Inventory WaitingRoom; do
  check "Dockerfile: $svc" test -f "services/$svc/Dockerfile"
done

if docker compose ps --status running 2>/dev/null | grep -q postgres; then
  n="$(docker compose exec -T postgres psql -U postgres -tAc \
      "SELECT count(*) FROM pg_database WHERE datname LIKE 'tickethive%'" 2>/dev/null | tr -d '[:space:]')"
  [[ "$n" == "7" ]] && ok "local databases: 7" || bad "local databases: ${n:-none} (expected 7)"

  t="$(docker compose exec -T kafka kafka-topics --bootstrap-server localhost:29092 --list 2>/dev/null | grep -c tickethive)"
  [[ "$t" == "8" ]] && ok "local topics: 8" || bad "local topics: ${t:-none} (expected 8)"
else
  warn "local stack is not running - 'docker compose up -d' to check databases and topics"
fi

# ----------------------------------------------------------------- phase 3 --
head "Phase 3 - Azure environment"

if ! az account show >/dev/null 2>&1; then
  warn "not logged in to Azure - skipping Phases 3 and 4"
else
  exists() { az resource show -g "$RG" -n "$2" --resource-type "$3" >/dev/null 2>&1 && ok "$1" || bad "$1"; }

  exists "resource group and registry"  tickethiveacr  Microsoft.ContainerRegistry/registries
  exists "virtual network"              tickethive-vnet Microsoft.Network/virtualNetworks
  exists "key vault"                    tickethive-kv  Microsoft.KeyVault/vaults
  exists "log analytics workspace"      tickethive-logs Microsoft.OperationalInsights/workspaces
  exists "application insights"         tickethive-insights Microsoft.Insights/components
  exists "container apps environment"   tickethive-env Microsoft.App/managedEnvironments
  exists "postgres server"              tickethive-pg  Microsoft.DBforPostgreSQL/flexibleServers

  admin="$(az acr show -g "$RG" -n tickethiveacr --query adminUserEnabled -o tsv 2>/dev/null)"
  [[ "$admin" == "false" ]] && ok "registry admin user is off" || bad "registry admin user is ON"

  pg_state="$(az postgres flexible-server show -g "$RG" -n tickethive-pg --query state -o tsv 2>/dev/null)"
  if [[ "$pg_state" == "Ready" ]]; then
    dbs="$(az postgres flexible-server db list -g "$RG" --server-name tickethive-pg \
           --query "length([?starts_with(name,'tickethive_')])" -o tsv 2>/dev/null)"
    [[ "$dbs" == "7" ]] && ok "azure databases: 7" || bad "azure databases: ${dbs:-unknown} (expected 7)"
  else
    warn "postgres is stopped - cannot list databases (start it to check)"
  fi

  secrets="$(az keyvault secret list --vault-name "$KV" --query "length(@)" -o tsv 2>/dev/null)"
  [[ "${secrets:-0}" -ge 23 ]] && ok "key vault secrets: $secrets" || bad "key vault secrets: ${secrets:-0} (expected 23 or more)"

  creds="$(az identity federated-credential list --identity-name tickethive-github-mi -g "$RG" --query "length(@)" -o tsv 2>/dev/null)"
  [[ "${creds:-0}" == "4" ]] && ok "github federated credentials: 4" || bad "github federated credentials: ${creds:-0} (expected 4)"

  # --------------------------------------------------------------- phase 4 --
  head "Phase 4 - Kafka broker"

  if az vm show -g "$RG" -n tickethive-kafka >/dev/null 2>&1; then
    ok "kafka vm exists"
    power="$(az vm get-instance-view -g "$RG" -n tickethive-kafka \
      --query "instanceView.statuses[?starts_with(code,'PowerState/')].displayStatus" -o tsv 2>/dev/null)"
    printf "        power state: %s\n" "$power"

    rules="$(az network nsg rule list -g "$RG" --nsg-name tickethive-kafka-nsg --query "[].name" -o tsv 2>/dev/null)"
    grep -q allow-kafka-from-apps <<<"$rules" && ok "broker port restricted to the apps subnet" || bad "missing broker port rule"
    grep -q allow-ssh-from-devops <<<"$rules" && ok "ssh restricted to one address" || bad "missing ssh rule"

    az keyvault secret show --vault-name "$KV" -n kafka-bootstrap-servers >/dev/null 2>&1 \
      && ok "broker address stored in key vault" || bad "broker address missing from key vault"
  else
    bad "kafka vm does not exist"
  fi

  head "Cost check - anything running right now"
  pg="$(az postgres flexible-server show -g "$RG" -n tickethive-pg --query state -o tsv 2>/dev/null)"
  vm="$(az vm get-instance-view -g "$RG" -n tickethive-kafka \
        --query "instanceView.statuses[?starts_with(code,'PowerState/')].code" -o tsv 2>/dev/null)"
  apps="$(az containerapp list -g "$RG" --query "length([?properties.template.scale.minReplicas>\`0\`])" -o tsv 2>/dev/null)"

  [[ "$pg" == "Stopped" ]] && ok "postgres stopped" || warn "postgres is $pg - billing"
  [[ "$vm" == "PowerState/deallocated" ]] && ok "kafka vm deallocated" || warn "kafka vm is ${vm#PowerState/} - billing"
  [[ "${apps:-0}" == "0" ]] && ok "no apps pinned above zero" || warn "$apps app(s) pinned - billing"
fi

# ------------------------------------------------------------------ result --
printf "\n${BOLD}%d passed, %d failed, %d warnings${OFF}\n" "$PASS" "$FAIL" "$WARN"
[[ "$FAIL" -eq 0 ]] && exit 0 || exit 1