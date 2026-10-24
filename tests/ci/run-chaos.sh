#!/usr/bin/env bash
set -euo pipefail

export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-ticketflow-e2e}"
compose=(docker compose -f docker-compose.yml -f docker-compose.e2e.yml)

seed_file=$(find output/e2e -name 'happy-path-one-time-check-in-seed.json' -print -quit)
if [[ -z "${seed_file:-}" ]]; then
  echo "The happy-path E2E seed is required before chaos recreation verification." >&2
  exit 1
fi

event_id=$(jq -r '.eventId' "$seed_file")
order_id=$(jq -r '.orderIds[0]' "$seed_file")
ticket_id=$(jq -r '.ticketIds[0]' "$seed_file")
seat_id=$(jq -r '.seatIds[0]' "$seed_file")
marker_suffix="${order_id//-/}-$(date +%s)"
rabbit_queue="phase12-chaos-$marker_suffix"
kafka_topic="phase12-chaos-$marker_suffix"
kafka_group="phase12-chaos-before-$marker_suffix"
keycloak_user="phase12-chaos-$marker_suffix"
redis_key="phase12:chaos:$marker_suffix"

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

service_container_id() {
  local service="$1"
  local container_id
  container_id=$("${compose[@]}" ps -q "$service")
  if [[ -z "$container_id" ]]; then
    echo "Compose service $service has no container in project $COMPOSE_PROJECT_NAME." >&2
    return 1
  fi

  local actual_project
  actual_project=$(docker inspect --format '{{ index .Config.Labels "com.docker.compose.project" }}' "$container_id")
  if [[ "$actual_project" != "$COMPOSE_PROJECT_NAME" ]]; then
    echo "Refusing to mutate $service from Compose project $actual_project; expected $COMPOSE_PROJECT_NAME." >&2
    return 1
  fi
  printf '%s' "$container_id"
}

wait_service_healthy() {
  local service="$1"
  for _ in $(seq 1 120); do
    local container_id
    container_id=$(service_container_id "$service")
    if [[ "$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id")" == "healthy" ]]; then
      return 0
    fi
    sleep 2
  done
  echo "Timed out waiting for Compose service $service to become healthy." >&2
  return 1
}

recreate_service() {
  local service="$1"
  local old_id
  old_id=$(service_container_id "$service")
  echo "Recreating $service in Compose project $COMPOSE_PROJECT_NAME..."
  "${compose[@]}" up -d --force-recreate --no-deps "$service"
  wait_service_healthy "$service"

  local new_id
  new_id=$(service_container_id "$service")
  if [[ "$old_id" == "$new_id" ]]; then
    echo "$service was not recreated; container ID remained $old_id." >&2
    return 1
  fi
  echo "$service container changed from $old_id to $new_id."
}

customer_token() {
  curl --fail --silent --show-error \
    --data-urlencode grant_type=password \
    --data-urlencode client_id=ticketflow-dev \
    --data-urlencode username=customer1 \
    --data-urlencode password='Customer1!Dev' \
    http://localhost:8180/realms/ticketflow/protocol/openid-connect/token | jq -er '.access_token'
}

keycloak_admin_token() {
  curl --fail --silent --show-error \
    --data-urlencode grant_type=password \
    --data-urlencode client_id=admin-cli \
    --data-urlencode username=admin \
    --data-urlencode password=admin \
    http://localhost:8180/realms/master/protocol/openid-connect/token | jq -er '.access_token'
}

wait_service_healthy redis
wait_service_healthy rabbitmq
wait_service_healthy kafka
wait_service_healthy keycloak

# Seed one broker-owned marker in every stateful service before any recreation. These markers
# distinguish actual volume durability from the application databases merely being intact.
"${compose[@]}" exec -T redis redis-cli SET "$redis_key" "$order_id" | grep -q '^OK$'
"${compose[@]}" exec -T redis redis-cli SAVE | grep -q '^OK$'

curl --fail --silent --show-error -u ticketflow:ticketflow \
  -X PUT -H 'content-type: application/json' \
  --data '{"durable":true,"auto_delete":false,"arguments":{}}' \
  "http://localhost:15672/api/queues/%2F/$rabbit_queue" >/dev/null
rabbit_publish=$(jq -nc --arg key "$rabbit_queue" --arg payload "$order_id" \
  '{properties:{delivery_mode:2},routing_key:$key,payload:$payload,payload_encoding:"string"}')
curl --fail --silent --show-error -u ticketflow:ticketflow \
  -X POST -H 'content-type: application/json' --data "$rabbit_publish" \
  http://localhost:15672/api/exchanges/%2F/amq.default/publish | jq -e '.routed == true' >/dev/null

"${compose[@]}" exec -T kafka /opt/kafka/bin/kafka-topics.sh \
  --bootstrap-server localhost:9092 --create --if-not-exists --topic "$kafka_topic" \
  --partitions 1 --replication-factor 1 --config retention.ms=86400000
printf '%s\n' "$order_id" | "${compose[@]}" exec -T kafka \
  /opt/kafka/bin/kafka-console-producer.sh --bootstrap-server localhost:9092 --topic "$kafka_topic"
kafka_before=$("${compose[@]}" exec -T kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server localhost:9092 --topic "$kafka_topic" --group "$kafka_group" \
  --from-beginning --max-messages 1 --timeout-ms 10000)
[[ "$kafka_before" == *"$order_id"* ]]

admin_token=$(keycloak_admin_token)
keycloak_payload=$(jq -nc --arg username "$keycloak_user" \
  '{username:$username,enabled:true,emailVerified:true}')
curl --fail --silent --show-error \
  -H "Authorization: Bearer $admin_token" -H 'content-type: application/json' \
  --data "$keycloak_payload" http://localhost:8180/admin/realms/ticketflow/users >/dev/null

recreate_service redis
[[ "$("${compose[@]}" exec -T redis redis-cli GET "$redis_key" | tr -d '\r')" == "$order_id" ]]

recreate_service rabbitmq
rabbit_state=$(curl --fail --silent --show-error -u ticketflow:ticketflow \
  "http://localhost:15672/api/queues/%2F/$rabbit_queue")
[[ "$(jq -r '.durable' <<<"$rabbit_state")" == "true" ]]
[[ "$(jq -r '.messages_ready' <<<"$rabbit_state")" == "1" ]]

recreate_service kafka
kafka_after=$("${compose[@]}" exec -T kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server localhost:9092 --topic "$kafka_topic" \
  --group "phase12-chaos-after-$marker_suffix" --from-beginning \
  --max-messages 1 --timeout-ms 10000)
[[ "$kafka_after" == *"$order_id"* ]]
group_state=$("${compose[@]}" exec -T kafka /opt/kafka/bin/kafka-consumer-groups.sh \
  --bootstrap-server localhost:9092 --describe --group "$kafka_group")
awk -v topic="$kafka_topic" '$2 == topic && $4 == 1 { found=1 } END { exit !found }' <<<"$group_state"

recreate_service keycloak
wait_http http://localhost:8180/realms/ticketflow/.well-known/openid-configuration
admin_token=$(keycloak_admin_token)
keycloak_state=$(curl --fail --silent --show-error -G \
  -H "Authorization: Bearer $admin_token" --data-urlencode "username=$keycloak_user" \
  --data-urlencode exact=true http://localhost:8180/admin/realms/ticketflow/users)
[[ "$(jq -r 'length' <<<"$keycloak_state")" == "1" ]]
[[ "$(jq -r '.[0].username' <<<"$keycloak_state")" == "$keycloak_user" ]]

token=$(customer_token)

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

"${compose[@]}" exec -T clickhouse clickhouse-client \
  --user ticketflow --password ticketflow --database analytics \
  --query "SELECT throwIf(count() != 1, 'analytics business id missing after Kafka recreation') FROM sales FINAL WHERE order_id = '$order_id'"

echo "Chaos recreation PASS: container IDs changed and Redis, RabbitMQ, Kafka, Keycloak plus business state survived."
