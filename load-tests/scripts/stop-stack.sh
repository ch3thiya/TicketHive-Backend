#!/usr/bin/env bash
# Stops the services and token issuer started by start-stack.sh.
# Usage:  ./scripts/stop-stack.sh          (infrastructure keeps running)
#         ./scripts/stop-stack.sh --all    (also stops the docker compose stack)
set -euo pipefail

LOAD_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$LOAD_DIR/.." && pwd)"
RUN_DIR="$LOAD_DIR/.run"

shopt -s nullglob
for pidfile in "$RUN_DIR"/*.pid; do
  pid="$(cat "$pidfile")"
  # `dotnet run` starts the service as a child process, so stop the children first.
  pkill -TERM -P "$pid" 2>/dev/null || true
  kill -TERM "$pid" 2>/dev/null || true
  echo "stopped $(basename "$pidfile" .pid)"
  rm -f "$pidfile"
done

if [[ "${1:-}" == "--all" ]]; then
  (cd "$REPO_ROOT" && docker compose stop)
fi
