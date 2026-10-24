#!/usr/bin/env bash
set +e

export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-ticketflow-e2e}"

artifact_dir="${1:-output/ci}"
mkdir -p "$artifact_dir"
compose=(docker compose -f docker-compose.yml -f docker-compose.e2e.yml)

"${compose[@]}" ps --all >"$artifact_dir/compose-ps.txt" 2>&1
"${compose[@]}" logs --no-color --timestamps >"$artifact_dir/compose.log" 2>&1
docker events --since 30m --until 0s >"$artifact_dir/docker-events.txt" 2>&1

curl --silent --show-error "http://localhost:16686/api/traces?service=ordering&limit=50" \
  >"$artifact_dir/jaeger-ordering-traces.json" 2>"$artifact_dir/jaeger-error.txt"
curl --silent --show-error http://localhost:19090/api/v1/rules \
  >"$artifact_dir/prometheus-rules.json" 2>"$artifact_dir/prometheus-error.txt"
curl --silent --show-error -u admin:ticketflow http://localhost:3300/api/v1/provisioning/alert-rules \
  >"$artifact_dir/grafana-alert-rules.json" 2>"$artifact_dir/grafana-error.txt"

{
  echo "== reservation allocations =="
  "${compose[@]}" exec -T postgres psql -U ticketflow -d ticketflow_reservation -v ON_ERROR_STOP=1 -c \
    "SELECT event_id, seat_id, order_id, owner_user_id, generation, state FROM reservation.seat_allocations ORDER BY updated_at DESC LIMIT 500;"
  echo "== duplicate active seat guard =="
  "${compose[@]}" exec -T postgres psql -U ticketflow -d ticketflow_reservation -v ON_ERROR_STOP=1 -c \
    "SELECT event_id, seat_id, count(*) FROM reservation.seat_allocations WHERE state IN ('Held','Confirmed') GROUP BY event_id, seat_id HAVING count(*) > 1;"
  echo "== payment attempts and refunds =="
  "${compose[@]}" exec -T postgres psql -U ticketflow -d ticketflow_payment -v ON_ERROR_STOP=1 -c \
    "SELECT order_id, state, gateway_reference, reconcile_attempts FROM payment.payment_attempts ORDER BY created_at DESC LIMIT 500;"
  "${compose[@]}" exec -T postgres psql -U ticketflow -d ticketflow_payment -v ON_ERROR_STOP=1 -c \
    "SELECT order_id, state, gateway_reference, reconcile_attempts FROM payment.payment_refunds ORDER BY created_at DESC LIMIT 500;"
  echo "== ticket issuance =="
  "${compose[@]}" exec -T postgres psql -U ticketflow -d ticketflow_ticketing -v ON_ERROR_STOP=1 -c \
    "SELECT order_id, seat_id, issuance_state, checked_in_at, revoked_at FROM ticketing.tickets ORDER BY issued_at DESC LIMIT 500;"
  echo "== analytics business IDs =="
  "${compose[@]}" exec -T clickhouse clickhouse-client --user ticketflow --password ticketflow --database analytics \
    --query "SELECT order_id, event_id, seat_count, amount, currency FROM sales FINAL ORDER BY completed_at DESC LIMIT 500 FORMAT PrettyCompact"
} >"$artifact_dir/db-invariants.txt" 2>&1

if compgen -G "output/e2e/*-seed.json" >/dev/null; then
  cp output/e2e/*-seed.json "$artifact_dir"/
fi
if compgen -G "output/flash-sale/*" >/dev/null; then
  mkdir -p "$artifact_dir/flash-sale"
  cp -R output/flash-sale/. "$artifact_dir/flash-sale"/
fi

exit 0
