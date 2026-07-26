#!/usr/bin/env bash
set -euo pipefail

export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-ticketflow-e2e}"

seed_file=$(find output/e2e -name 'happy-path-one-time-check-in-seed.json' -print -quit)
if [[ -z "${seed_file:-}" ]]; then
  echo "The happy-path E2E seed is required before chaos restart verification." >&2
  exit 1
fi

event_id=$(jq -r '.eventId' "$seed_file")
order_id=$(jq -r '.orderIds[0]' "$seed_file")
ticket_id=$(jq -r '.ticketIds[0]' "$seed_file")
seat_id=$(jq -r '.seatIds[0]' "$seed_file")

wait_http() {
  local url="$1"
  for _ in $(seq 1 120); do
    if curl --fail --silent "$url" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  echo "Timed out waiting for $url" >&2
  return 1
}

wait_http_auth() {
  local url="$1"
  local bearer_token="$2"
  for _ in $(seq 1 120); do
    if curl --fail --silent -H "Authorization: Bearer $bearer_token" "$url" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  echo "Timed out waiting for authenticated endpoint $url" >&2
  return 1
}

for container in ticketflow-redis ticketflow-rabbitmq ticketflow-kafka ticketflow-keycloak; do
  echo "Restarting $container with its existing persistent volume..."
  docker restart "$container"
  case "$container" in
    ticketflow-keycloak)
      wait_http http://localhost:8180/realms/ticketflow/.well-known/openid-configuration ;;
    *)
      wait_http http://localhost:8080/health/live ;;
  esac
done

token=$(curl --fail --silent --show-error \
  --data-urlencode grant_type=password \
  --data-urlencode client_id=ticketflow-dev \
  --data-urlencode username=customer1 \
  --data-urlencode password='Customer1!Dev' \
  http://localhost:8180/realms/ticketflow/protocol/openid-connect/token | jq -r '.access_token')

# Gateway service routes are authenticated by design. An anonymous curl reports 401 even when
# the service itself is Ready, so probe them with the restored Keycloak user's bearer token.
wait_http_auth http://localhost:8080/ordering/health/ready "$token"
wait_http_auth http://localhost:8080/reservation/health/ready "$token"
wait_http_auth http://localhost:8080/ticketing/health/ready "$token"
wait_http_auth http://localhost:8080/analytics/health/ready "$token"

order=$(curl --fail --silent --show-error -H "Authorization: Bearer $token" \
  "http://localhost:8080/ordering/orders/$order_id")
[[ "$(jq -r '.status' <<<"$order")" == "Confirmed" ]]

availability=$(curl --fail --silent --show-error -H "Authorization: Bearer $token" \
  "http://localhost:8080/reservation/events/$event_id/availability")
[[ "$(jq -r --arg seat "$seat_id" '.seats[] | select(.id == $seat) | .status' <<<"$availability")" == "Sold" ]]

tickets=$(curl --fail --silent --show-error -H "Authorization: Bearer $token" \
  "http://localhost:8080/ticketing/orders/$order_id/tickets")
[[ "$(jq -r 'length' <<<"$tickets")" == "1" ]]
[[ "$(jq -r '.[0].id' <<<"$tickets")" == "$ticket_id" ]]

curl --fail --silent --show-error -H "Authorization: Bearer $token" \
  "http://localhost:8080/ticketing/tickets/$ticket_id/pdf" --output output/e2e/chaos-recovered-ticket.pdf
grep -a -q '^%PDF-' output/e2e/chaos-recovered-ticket.pdf

docker exec ticketflow-clickhouse clickhouse-client --user ticketflow --password ticketflow --database analytics \
  --query "SELECT throwIf(count() != 1, 'analytics business id missing after Kafka restart') FROM sales FINAL WHERE order_id = '$order_id'"

echo "Chaos restart PASS: order, sold allocation, ticket PDF, Keycloak user and analytics ID survived."
