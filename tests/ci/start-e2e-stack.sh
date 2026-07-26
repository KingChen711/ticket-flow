#!/usr/bin/env bash
set -euo pipefail

export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-ticketflow-e2e}"

compose=(docker compose -f docker-compose.yml -f docker-compose.e2e.yml)
services=(
  catalog identity reservation ordering notification search payment ticketing
  recommendation analytics gateway keycloak e2e-functions prometheus grafana jaeger
)

"${compose[@]}" up -d --build "${services[@]}"

# Named volumes intentionally survive ordinary development restarts. Re-run the idempotent
# database bootstrap so an older volume created before a service/database was introduced can
# still execute the current E2E suite. Keycloak may have exited while the missing database was
# being repaired, so explicitly bring it back afterwards.
docker exec -i ticketflow-postgres psql -v ON_ERROR_STOP=1 -U ticketflow -d ticketflow \
  < infrastructure/postgres/init/01-create-databases.sql
"${compose[@]}" up -d keycloak

wait_http() {
  local url="$1"
  local label="$2"
  for _ in $(seq 1 120); do
    if curl --fail --silent --show-error "$url" >/dev/null 2>&1; then
      echo "$label is ready: $url"
      return 0
    fi
    sleep 2
  done
  echo "Timed out waiting for $label: $url" >&2
  return 1
}

wait_http http://localhost:8080/health/live gateway
wait_http http://localhost:8180/realms/ticketflow/.well-known/openid-configuration keycloak
wait_http http://localhost:3300/api/health grafana
wait_http http://localhost:19090/-/ready prometheus

# A live process is not enough for E2E: wait until the routed catalog and gateway readiness
# endpoints answer. The PDF assertion later proves the production render function provider is
# reachable through Ticketing without weakening the authenticated Ticketing route.
wait_http http://localhost:8080/catalog/events gateway-catalog-route
wait_http http://localhost:8080/health/ready gateway-readiness

for _ in $(seq 1 60); do
  if [[ "$(docker inspect --format '{{.State.Health.Status}}' ticketflow-e2e-functions)" == "healthy" ]]; then
    echo "e2e-functions is healthy"
    exit 0
  fi
  sleep 2
done

echo "Timed out waiting for e2e-functions container health" >&2
exit 1
