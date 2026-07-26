---
title: "Runbook — Phase 12 release gates and failure evidence"
updated: 2026-07-26
tags: [ticketflow, runbook, phase-12, ci, e2e, chaos]
---

# Runbook — Phase 12 release gates

## Gate order

Run cheap gates first and do not continue past a red stage:

```powershell
Compare-Object (Get-Content infrastructure/keycloak/realm-export.json) (Get-Content infrastructure/k8s/ticketflow/files/realm-export.json)
pnpm install --frozen-lockfile
pnpm lint
pnpm typecheck
pnpm test:web
pnpm build
pnpm test:e2e:frontend
dotnet test TicketFlow.slnx
dotnet test tests/TicketFlow.Phase12.HardeningTests/TicketFlow.Phase12.HardeningTests.csproj --filter "Category=Phase12Harness"
```

The frontend Playwright command needs `pnpm exec playwright install chromium` once per machine.
Its server lifecycle is owned by `playwright.config.ts`; do not manually leave three Next servers
running in CI. Full-stack PDF/QR verification also needs Poppler's `pdftoppm`; set
`E2E_PDFTOPPM` to its absolute executable path when it is not on `PATH` (the Linux CI installs
`poppler-utils`).

## Full-stack gate

The E2E overlay replaces only the external Lambda control plane and still executes the production
PDF/QR function against MinIO:

```powershell
$env:COMPOSE_PROJECT_NAME = 'ticketflow-e2e'
bash tests/ci/start-e2e-stack.sh
pnpm test:e2e:full
```

The suite writes non-secret reproduction seeds, PDF/PNG inputs, traces and browser recordings below
`output/`. It covers authenticated UI login, saleable event fixture, durable hold, three submissions
with one idempotency key, confirmed order, PDF download, first/repeat scan, cancellation/refund
revocation, and Reservation outage/recovery.

The overlay raises the per-identity hold cap because the load harness deliberately represents all
virtual users with one synthetic customer token. This exception is scoped to the isolated
`ticketflow-e2e` Compose project; normal development and production retain the anti-abuse defaults.
Always tear the test stack down with the same project name so no developer volume is selected:

```powershell
$env:COMPOSE_PROJECT_NAME = 'ticketflow-e2e'
docker compose -f docker-compose.yml -f docker-compose.e2e.yml down --volumes --remove-orphans
```

Run the flash-sale ID reconciliation with dev passwords supplied through environment variables as
shown in [the load-test README](../tests/TicketFlow.FlashSale.LoadTests/README.md). A passing total
is insufficient: `flash-sale-summary.json.businessInvariant` must contain identical order ID sets
and identical allocation/ticket seat ID sets.

## Failure collection and triage

```powershell
bash tests/ci/collect-evidence.sh output/ci/manual
```

The collector saves Compose status/logs, Docker events, Ordering traces, Prometheus/Grafana rule
state, Postgres allocation/payment/ticket rows, ClickHouse business IDs and E2E seeds. Start triage
with the first mismatching business ID, then follow its trace and the matching runbook:

- `Unknown`/refund ambiguity or saga `ManualReview`: [stuck orders](runbook-stuck-orders.md).
- Cache/broker/identity restart: [stateful recovery](runbook-stateful-recovery.md).
- Browser failure: Playwright trace plus the seed file; rerun the single test with `--grep`.

Never delete or hand-edit a receipt to make a gate green. Redrive/reconcile by the existing
operation key, and preserve the original artifact bundle.

## Chaos gate

Chaos runs only on a disposable E2E stack. `tests/ci/run-chaos.sh` requires the happy-path seed,
restarts the exact Redis/RabbitMQ/Kafka/Keycloak containers one at a time, waits for readiness, then
re-reads the same order, seat, ticket/PDF and analytics ID. It never targets an arbitrary Docker
context or Kubernetes namespace.
