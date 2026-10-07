#!/usr/bin/env bash
# Rebuilds the MatMail container and redeploys the dev stack (live-reload / test workflow).
#   ./scripts/redeploy.sh          rebuild + recreate, keep data
#   ./scripts/redeploy.sh --fresh  also delete the data and database volumes
set -euo pipefail
cd "$(dirname "$0")/.."

export MATMAIL_CHANNEL=local
export MATMAIL_BUILD_DATE="$(date -u +%Y%m%d)"

if [[ "${1:-}" == "--fresh" ]]; then
  docker compose -f docker-compose.dev.yml down -v
fi

docker compose -f docker-compose.dev.yml up -d --build --remove-orphans
echo
echo "MatMail local-${MATMAIL_BUILD_DATE} is running on http://localhost:9933"
