---
title: "ADR 001: Authoritative state and durable relays for purchase correctness"
status: accepted
date: 2026-07-26
---

# ADR 001: Authoritative state and durable relays for purchase correctness

## Context

The pre-Phase-12 purchase path crossed Redis, PostgreSQL, Marten, RabbitMQ, a payment provider,
MinIO/Lambda and Kafka. Several steps used read-then-write checks or save-then-publish sequences.
The browser also supplied price/seat fields that downstream services could accidentally trust.
Those boundaries allowed oversell, stale compensation, double charge, duplicate order streams,
lost ticket artifacts and orders that stayed pending forever after a partial failure.

## Decision

1. PostgreSQL `SeatAllocation` plus its owner/generation token is authoritative for a seat. Redis
   is a rebuildable compare-and-set cache, never an independent winner.
2. Checkout accepts `holdId + idempotencyKey`; Ordering reads the persisted Reservation quote and
   uses one version-checked milestone writer for the Marten stream.
3. Every external side effect has a durable operation record before invocation and a stable key:
   checkout dispatch, payment/refund, issuance, notification and analytics relay.
4. Ambiguous provider outcomes are `Unknown`, reconciled by lookup and escalated to
   `ManualReview`; they are never converted into decline/compensation by exception handling.
5. Canonical order completion is independent of Kafka. Analytics is a retryable downstream relay.
6. Authorization is checked again at the owning service using authenticated subject, role,
   service audience and resource owner. Scanner event context is an authorization boundary.
7. Release admission is evidence-driven: fast/contract → integration/concurrency → authenticated
   E2E/flash IDs → scheduled chaos, with failure artifacts sufficient to reproduce the same seed.

## Consequences

The system stores more receipts and projections, and operators must handle explicit unknown/manual
review states. That cost is accepted because deleting uncertainty or retrying blindly would be less
honest and can lose seats or money. Redis can be rebuilt, brokers can redeliver, and provider calls
can be repeated safely. The narrow produce-success/marker-failure window can still duplicate one
Kafka record; ClickHouse's `ReplacingMergeTree` keyed by `OrderId` makes the consumer projection
idempotent, and the limitation remains documented rather than called exactly-once.

For CI, a test-only service implements only the Lambda Invoke control-plane route and executes the
production PDF function against MinIO. It avoids a personal LocalStack token without replacing the
QuestPDF/QR/object-storage behavior under test. Production and normal deployment configuration
continue to use the external Functions provider.
