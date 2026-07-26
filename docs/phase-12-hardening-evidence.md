---
title: "Phase 12 hardening evidence"
updated: 2026-07-26
tags: [ticketflow, phase-12, evidence, e2e, chaos, release]
---

# Phase 12 hardening evidence

## Admission status

The current uncommitted release candidate passed the local gates below on Windows 11, .NET
10.0.8, Node 24, pnpm 11.1.2 and Docker Desktop 29.4.3. The run used the isolated Compose project
`ticketflow-e2e`; its disposable containers and volumes were removed after evidence collection.
The normal `ticket-flow_*` development volumes were not selected.

**Remote `main` admission remains pending.** This work was intentionally not committed or pushed,
so there cannot yet be a clean-checkout GitHub Actions run URL for this exact tree. Phase 12 may be
called locally release-ready, but not remotely admitted, until the user commits/pushes and the
`main` workflow succeeds.

## Gate results — 2026-07-26

| Gate | Command / probe | Result |
|---|---|---|
| Frozen frontend | `pnpm install --frozen-lockfile` | pass |
| Frontend static | `pnpm lint`; `pnpm typecheck` | pass |
| Web contracts | `pnpm test:web` | **11/11**, 0 skipped |
| Production zones | `pnpm build` | customer, organizer and scanner pass |
| Multi-zone browser contract | `pnpm test:e2e:frontend` | **1/1** |
| Backend regression | `dotnet test TicketFlow.slnx -c Release --no-restore` | **88/88**, 0 skipped |
| Full hardening, run 1 | `dotnet test ...Phase12.HardeningTests... --filter "Category=Phase12Harness"` | **201/201**, 0 skipped, 1m31s |
| Full hardening, run 2 | same command, fresh Testcontainers | **201/201**, 0 skipped, 1m30s |
| Authenticated full stack | Playwright `full-stack` project | **3/3**: purchase/scanner, cancel/refund/revoke, Reservation outage/recovery |
| Flash sale | NBomber default shape | **300/300** attempts; 50 Confirmed; 250 expected seat conflicts; 0 unexpected; no oversell |
| Stateful restart | Redis → RabbitMQ → Kafka → Keycloak, same volumes | pass; same order, allocation, ticket/PDF, identity and analytics ID |
| Observability | Grafana/Prometheus APIs | dashboard present; 5 alerts including all 3 Phase 12 UIDs; 11 active targets; 2 backlog series at 0 |
| Invariant artifact | `tests/ci/collect-evidence.sh output/ci/full-stack` | logs, Docker events, Jaeger traces, rules, DB rows, ClickHouse IDs and seeds captured |

The hardening TRX files are `output/test-results/hardening/phase12-harness-{1,2}.trx`.
The full-stack artifact snapshot is under `output/ci/full-stack/`. Both locations are ignored build
evidence, not source-controlled fixtures; CI uploads their equivalent for 14/30 days.

## Reproducible business IDs

Happy-path seed:

- Event `89251e94-e0c1-4926-8ddf-fa99dfba316a`
- Order `a8bd92f3-a74b-4243-8f87-21cedc6394ca`
- Seat `2efbaee7-491f-4e08-b4e4-fbaacc8d1c7b`
- Ticket `636a95ad-0907-4a17-8c12-6b6f81000c33`

Cancellation seed:

- Event `e17d11b7-1b9f-414c-8a02-2fe5d26884c0`
- Order `11d6dbf7-976d-4169-84aa-3832431f5319`
- Ticket `02b5ca0a-d31e-48bb-b13b-fd09bf8677e1`
- Workflow `dbbf22b0-8838-48c0-a4e3-e1456c178e78`

Flash-sale event `08edb2f5-44e4-4b28-952b-90a5bfde577e` reconciled exact sets of **50**
confirmed orders, confirmed allocations, authorized payments, issued tickets and ClickHouse
`sales FINAL` rows. Allocation and ticket seat sets were also identical at **50**. The independent
duplicate-active-allocation query returned **0 rows**.

Checksums make the local evidence bundle identifiable:

| Artifact | SHA-256 |
|---|---|
| `output/flash-sale/flash-sale-summary.json` | `59FCD4EB6CBFDB143AEAAD13EE497848C32E1A0ED62D03A0ED88C6758FDCC92B` |
| `output/e2e/happy-path-one-time-check-in-seed.json` | `F0DB59CC7DF2BD0FE3B52AF210D156758C61DA15E4216B70485A199A3D3CACDA` |
| `output/e2e/event-cancellation-refund-seed.json` | `A1B090F67745C2E7336FD62892C99AFC3D4ED7125FC0C5FBBDC3FDAAFC211AB0` |
| `output/e2e/chaos-recovered-ticket.pdf` | `2E02A54AF0D176C643A8F158E672754D0BED93193B02E1928B34DDDF92722F73` |

## Final DoD traceability

| Invariant / DoD | Evidence | Status |
|---|---|---|
| Client cannot change seats/price/currency/owner | Hardening tamper/authority tests; full-stack checkout sends only `holdId`, payment method and idempotency key | pass |
| No oversell or stale-owner release | 201-case concurrency suite twice; 300:50 flash sale; zero duplicate active allocations; cancellation-restart regression | pass |
| Charge/refund effectively once | counting-provider duplicate/response-loss/reconciliation tests; cancellation E2E reaches refund and revoked ticket | pass |
| Order stream monotonic and checkout idempotent | Marten permutation/expected-version suite; two simultaneous plus one browser checkout return one `OrderId` | pass |
| Saga cannot wait forever | deadline/redelivery/manual-review tests in both 201-case runs; alert UIDs provisioned | pass |
| Ticket safe and deterministic | production render function through Lambda-compatible provider; PDF rasterized; first scan Valid, replay AlreadyCheckedIn, cancelled ticket Revoked | pass |
| Projections do not resurrect | revision/tombstone permutation tests in hardening gate | pass |
| Notification/provider ambiguity is durable | provider ledger/outbox/retry tests and production fail-closed configuration contracts | pass |
| Resource authorization is owned by each service | anonymous/cross-role/cross-owner/service-account matrices plus authenticated scanner event boundary | pass |
| Kafka/topic/migration startup is deterministic | Commit 17 clean-kind provision/migration evidence remains unchanged; configuration job retains Helm gates | pass |
| Stateful restart preserves correctness | four-container restart drill re-read the exact happy-path IDs and PDF; Analytics row count stayed exactly one | pass |
| CI protects `main` and retains evidence | workflow triggers `push`/`pull_request` on `main`, stages by cost, runs hardening twice, uploads artifacts and schedules chaos | implemented; remote run pending |

## Failures found by the release gate

The gate was allowed to fail until its own assumptions were correct. It found and fixed:

1. h2c gRPC call credentials were rejected, causing every real checkout to return 400.
2. simultaneous same-key Marten checkout could surface `DocumentAlreadyExistsException` as 500.
3. the Lambda adapter used the wrong function name/content binder, so PDF Invoke returned 415.
4. event publication and Reservation sale-policy consumption raced; fixtures now use a hold/release
   readiness probe instead of sleeping.
5. restarting Reservation after a legitimate cancellation treated an exact `Released` allocation
   set as ambiguous authority and exited fail-closed. Reconciliation now accepts exact terminal
   released history while still rejecting partial/mixed ownership, with an integration regression.
6. the flash harness sent the obsolete client-authoritative checkout body and counted every 409 as
   sold out. It now uses the durable-hold contract and parses the rejection reason.
7. one synthetic load identity tripped production anti-abuse caps before exercising contention.
   Only the isolated E2E overlay raises those caps; normal/production defaults remain unchanged.
8. chaos readiness probes were anonymous through an authenticated Gateway route and waited on
   healthy services as 401. They now obtain the restored Keycloak token first.
9. two concurrency tests sampled before all writers were actually at the barrier; explicit
   synchronization removed the flake without weakening an assertion.

Architecture and operational interpretation are captured in [architecture.md](architecture.md),
[ADR 001](adr/001-phase-12-authoritative-state-and-durable-relays.md), and the
[release-gate runbook](runbook-phase-12-release-gates.md).
