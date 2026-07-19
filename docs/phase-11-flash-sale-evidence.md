# Phase 11 Commit 4 — Flash-sale load evidence

**Run:** 2026-07-19 07:04:52–07:05:20 UTC (14:04:52–14:05:20 UTC+07:00)<br>
**Environment:** Compose stack only. The kind control-plane container was stopped before the
run; the Compose stack and kind were never active together. Unrelated LocalStack reminder/timer
Lambdas used a 24-hour schedule during the run, leaving ticket PDF rendering as the only Lambda
load. The test still executed the real ticketing PDF Lambda.

## Scenario and NBomber result

`TicketFlow.FlashSale.LoadTests` created a new published catalog event with 50 seats, provisioned
Reservation inventory through the gateway, then activated 300 NBomber virtual users. Each user
attempted `hold -> POST /ordering/orders`; PurchaseSaga drove confirmation, payment, ticket
issuance, and the real LocalStack PDF render.

| Measure | Result |
|---|---:|
| NBomber virtual users / total iterations | 300 / 300 |
| Physical gateway connection pool | 64 |
| Orders accepted (`201`) | 50 |
| Expected sold-out holds (`409`) | 250 |
| NBomber failures | 0 |
| p50 / p95 / p99 full iteration latency | 343.55 / 1,984.51 / 2,028.54 ms |
| Confirmed orders after saga settlement | 50 |
| Cancelled or pending accepted orders | 0 |
| Reservation seats sold | 50 / 50 |

The 64-socket cap is deliberate and documented in the runner: Docker Desktop's Windows
host-port NAT dropped a larger burst before requests reached the gateway. NBomber still activates
all 300 virtual users; excess users wait for a client connection instead of becoming a false
network failure.

NBomber session: `2026-07-19_07-04-52_d619afe1`. The local HTML, CSV, Markdown, text, and JSON
reports are generated under `artifacts/phase-11-flash-sale/` (ignored from Git because they are
machine-run artifacts).

## Independent data-store reconciliation

Run event ID: `8966d247-e552-4bb7-8875-4441b4160811`.

| Source queried after the run | Orders | Seats | Amount |
|---|---:|---:|---:|
| PostgreSQL `ticketflow_ordering.public.mt_doc_myorderview`, `Status = Confirmed` | 50 | 50 | — |
| PostgreSQL `ticketflow_reservation.reservation.seats`, `Status = Sold` | — | 50 | — |
| ClickHouse `analytics.sales FINAL` | 50 rows | 50 | ₫5,000,000 |

All three stores agree with the 50-seat capacity. This is the full-stack oversell invariant:
**sold seats never exceeded 50, and the final accepted total was exactly 50.**

## Observability evidence

- Grafana's `ticketflow-overview` dashboard obtains its tickets/minute panel from
  `sum(rate({"ticketflow.tickets.sold_total"}[1m])) * 60`. Prometheus recorded the final
  `ticketflow.tickets.sold_total` value as **50**; the dashboard's historical one-minute rate
  reached **35.37 tickets/minute** after scrape interpolation.
- The Payment circuit-breaker query
  `sum(increase({"resilience.polly.strategy.events_total", "event.name"="OnCircuitOpened"}[5m]))`
  returned no series, i.e. **zero circuit-open events** during the run.
- Jaeger trace `a836de42f17c6808331251092005eed8` is the slowest sampled gateway order trace:
  **26,454.08 ms**, 45 spans, spanning `gateway`, `reservation`, `ordering`, `payment`,
  `ticketing`, `analytics`, `notification`, and `recommendation`. Open it locally at
  `http://localhost:16686/trace/a836de42f17c6808331251092005eed8`.

The browser bridge available to this workspace failed to initialize before a visual screenshot
could be captured. The dashboard's real Prometheus measurements above and the real Jaeger trace
are retained as the reproducible evidence; this is a tooling limitation, not a dashboard query
failure.

## Load-discovered hardening included in this commit

- Database-backed services have bounded Npgsql pools totaling 80 connections (below the
  Compose Postgres server's default 100-connection limit), preventing a flash-sale request burst
  from exhausting Postgres.
- Payment consumers run at concurrency 8; ticket PDF rendering runs at concurrency 1 on this
  constrained local LocalStack executor, with RabbitMQ providing the durable buffer.
- Ticket-issued and completed order audit events are appended atomically by
  `OrderCompletedConsumer`, removing the cross-queue Marten stream-version race exposed by the
  initial burst.
