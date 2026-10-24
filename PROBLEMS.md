## P0 — release blockers

1. **Orders are not bound to the seats in their hold.** Reservation confirms the hold’s seats, but the saga charges, tickets, and compensates using client-supplied seat IDs. A buyer can hold seat A and obtain a ticket for seat B; compensation can also release B incorrectly. Evidence: [ConfirmSeatsConsumer.cs (line 18)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Reservation/TicketFlow.Reservation.Api/Consumers/ConfirmSeatsConsumer.cs:18), [PurchaseSaga.cs (line 69)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ordering/TicketFlow.Ordering.Application/Sagas/PurchaseSaga.cs:69).  
    **Solution:** Persist hold owner, event, exact seats, and generation. Confirm via atomic set equality, replace saga state with the authoritative confirmed seats, and compensate only allocations owned by that order.
    
2. **Price and currency are client-authoritative.** Editable query/hidden fields become the persisted and charged amount without server repricing. Currency then disappears from messaging and Payment defaults to VND; analytics aggregates amounts without currency separation. Evidence: [checkout/actions.ts (line 16)](D:/code/Projects/roadmap-projects/ticket-flow/apps/customer-web/src/app/checkout/actions.ts:16), [PlaceOrderCommand.cs (line 29)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ordering/TicketFlow.Ordering.Application/Orders/PlaceOrderCommand.cs:29), [ProcessPaymentConsumer.cs (line 41)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Payment/TicketFlow.Payment.Api/Consumers/ProcessPaymentConsumer.cs:41).  
    **Solution:** Checkout should accept only `holdId`, payment selection, and an idempotency key. Recalculate from a versioned catalog price snapshot, propagate validated ISO currency end-to-end, and group analytics by currency.
    
3. **Draft, cancelled, and expired events remain purchasable.** Public catalog queries do not filter lifecycle status, and neither holds nor order placement enforce saleability. Cancelling an event does not refund orders, release seats, or revoke tickets. Evidence: [EventRepository.cs (line 9)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Catalog/TicketFlow.Catalog.Infrastructure/Repositories/EventRepository.cs:9), [EventService.cs (line 114)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Catalog/TicketFlow.Catalog.Application/Events/EventService.cs:114).  
    **Solution:** Maintain an authoritative sale-policy snapshot in Reservation/Ordering and introduce a durable event-cancellation workflow covering holds, paid orders, refunds, and ticket revocation.
    
4. **Order creation and saga initiation are a dual write.** Marten commits the pending order before publishing `OrderSubmitted`. A crash between them strands the order; a browser retry creates another order for the same hold. Evidence: [PlaceOrderCommand.cs (line 34)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ordering/TicketFlow.Ordering.Application/Orders/PlaceOrderCommand.cs:34).  
    **Solution:** Enforce a checkout idempotency key and unique hold-to-order mapping. Persist all initiation data with an outbox/checkpointed Marten subscription and return the original result on matching retries.
    
5. **Reservation confirmation destructively updates Redis before Postgres and messaging.** After Redis deletes the hold and marks seats sold, a database, SignalR, or publishing failure cannot reconstruct the outcome; redelivery can report failure for seats already sold. Evidence: [ReservationService.cs (line 52)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Reservation/TicketFlow.Reservation.Application/Seats/ReservationService.cs:52), [ConfirmHoldScript.cs (line 28)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Reservation/TicketFlow.Reservation.Infrastructure/Redis/ConfirmHoldScript.cs:28).  
    **Solution:** Add a durable confirmation receipt keyed by order/hold, plus transactional inbox/outbox and reconciliation. SignalR notification must be non-critical.
    
6. **Seat compensation has an ABA ownership race.** Redis and Postgres store only “sold,” not who owns the sale. A delayed `ReleaseSeats` for order A can make a seat currently owned by order B available. Redis is also modified before Postgres. Evidence: [ReservationService.cs (line 72)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Reservation/TicketFlow.Reservation.Application/Seats/ReservationService.cs:72).  
    **Solution:** Store allocation owner and generation in both stores and use conditional compare-and-release operations. Make compensation idempotent against the original allocation record.
    
7. **Payment can charge more than once.** Consumers check the database before calling the provider; concurrent messages can both charge and only then race on a unique index. Ambiguous timeouts are retried as fresh charges, while transport failures are recorded as definitive declines. Refund handling only changes the local database—it never refunds the provider. Evidence: [ProcessPaymentConsumer.cs (line 29)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Payment/TicketFlow.Payment.Api/Consumers/ProcessPaymentConsumer.cs:29), [RefundPaymentConsumer.cs (line 25)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Payment/TicketFlow.Payment.Api/Consumers/RefundPaymentConsumer.cs:25).  
    **Solution:** Require provider-side idempotency keyed by order, atomically claim a pending attempt before charging, serialize by order, represent ambiguous results as `Unknown`, reconcile them, and implement idempotent provider refunds.
    
8. **Independent Ordering consumers can reorder and regress the event stream.** Seat and payment milestones arrive on separate queues and append without expected stream versions. A late `SeatsConfirmed` can overwrite `PaymentAuthorized`, causing completion to fail permanently. Duplicates and direct admin confirm/cancel commands have the same problem, and admin cancellation does not stop the saga. Evidence: [OrderSeatsConfirmedConsumer.cs (line 31)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ordering/TicketFlow.Ordering.Api/Consumers/OrderSeatsConfirmedConsumer.cs:31), [MartenOrderStore.cs (line 34)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ordering/TicketFlow.Ordering.Infrastructure/MartenOrderStore.cs:34).  
    **Solution:** Use one partitioned writer per order, inbox/business-event deduplication, expected stream versions, and monotonic domain transitions. Route administrative decisions through the process manager.
    
9. **The purchase saga has an early-response race and no recovery deadlines.** It can publish before its state is safely visible; the project’s own phase notes document orders stuck because a fast response was dropped. Every awaiting/compensating state can then remain forever. Kafka analytics is also executed before business completion, so an analytics outage strands already-paid and ticketed orders. Evidence: [PurchaseSaga.cs (line 65)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ordering/TicketFlow.Ordering.Application/Sagas/PurchaseSaga.cs:65), [phase-5-plan.md (line 237)](D:/code/Projects/roadmap-projects/ticket-flow/docs/phase-5-plan.md:237).  
    **Solution:** Persist state and outbox before sending, redeliver missing-instance responses, add per-state timeouts and reconciliation, commit one canonical completion event, and relay RabbitMQ/Kafka outputs independently.
    
10. **Ticket issuance is another object-store/database dual write.** PDFs are uploaded before ticket rows commit. Partial failures leave orphan artifacts, concurrent issuance races can report failure despite another successful issuer, and all technical exceptions are swallowed and converted immediately into business failure. Evidence: [IssueTicketsConsumer.cs (line 43)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ticketing/TicketFlow.Ticketing.Api/Consumers/IssueTicketsConsumer.cs:43).  
    **Solution:** Persist and uniquely claim an issuance job first, render deterministically, retry transient failures, and reconcile/clean artifacts before emitting terminal failure.
    
11. **Concurrent scans can both admit the same ticket.** Validation uses read-check-write without a concurrency token or conditional update. Tickets also lack event/gate context. Evidence: [TicketEndpoints.cs (line 43)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ticketing/TicketFlow.Ticketing.Api/Endpoints/TicketEndpoints.cs:43).  
    **Solution:** Use an atomic `UPDATE … WHERE checked_in_at IS NULL RETURNING`, store event and revocation state, and authorize scanners against assigned gates/events.
    
12. **Correctness-critical infrastructure is ephemeral.** Redis, RabbitMQ, Kafka, and Keycloak have no persistent storage in the supplied deployment. Restarting them can lose sold markers, saga messages, stream offsets, or registered users. Evidence: [redis.yaml (line 1)](D:/code/Projects/roadmap-projects/ticket-flow/infrastructure/k8s/ticketflow/templates/redis.yaml:1), [rabbitmq.yaml (line 1)](D:/code/Projects/roadmap-projects/ticket-flow/infrastructure/k8s/ticketflow/templates/rabbitmq.yaml:1), [kafka.yaml (line 1)](D:/code/Projects/roadmap-projects/ticket-flow/infrastructure/k8s/ticketflow/templates/kafka.yaml:1), [keycloak.yaml (line 14)](D:/code/Projects/roadmap-projects/ticket-flow/infrastructure/k8s/ticketflow/templates/keycloak.yaml:14).  
    **Solution:** Use managed stores or StatefulSets/PVCs, durable/quorum queues, Keycloak Postgres, Redis persistence, and deterministic reconstruction from the durable allocation ledger.
    

## P1 — high-priority defects

13. **Ticket APIs are IDOR-vulnerable.** Any authenticated user can list another order’s tickets or download another ticket PDF because ownership is neither stored nor checked. Evidence: [TicketEndpoints.cs (line 16)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Ticketing/TicketFlow.Ticketing.Api/Endpoints/TicketEndpoints.cs:16).  
    **Solution:** Store customer/event ownership and scope every query to the authenticated subject, with an explicit administrative override.
    
14. **Organizer authorization is global rather than resource-scoped.** Any organizer can mutate any event, see global revenue, or validate unrelated tickets because events have no owner/tenant. Evidence: [EventEndpoints.cs (line 12)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Catalog/TicketFlow.Catalog.Api/Endpoints/EventEndpoints.cs:12), [Event.cs (line 9)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Catalog/TicketFlow.Catalog.Domain/Events/Event.cs:9).  
    **Solution:** Persist organizer/tenant ownership and gate assignments and apply resource-based authorization throughout Catalog, Analytics, Media, Reservation, and Ticketing.
    
15. **Reservation’s service trust boundary is bypassable.** The service itself registers no authentication, is host-exposed in Compose, and gateway inventory provisioning requires only authentication. Capacity is unbounded and directly allocates memory. Evidence: [Program.cs (line 29)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Reservation/TicketFlow.Reservation.Api/Program.cs:29), [InventoryEndpoints.cs (line 12)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Reservation/TicketFlow.Reservation.Api/Endpoints/InventoryEndpoints.cs:12).  
    **Solution:** Authenticate at the service, require organizer/event ownership, use service credentials or mTLS internally, add network policies, and cap all inventory dimensions.
    
16. **Inventory provisioning commits Postgres before loading Redis.** A crash leaves durable inventory that retries refuse to repair, so every hold becomes invalid. Evidence: [InventoryService.cs (line 19)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Reservation/TicketFlow.Reservation.Application/Seats/InventoryService.cs:19).  
    **Solution:** Make provisioning an idempotent “ensure and rebuild” operation backed by an outbox/reconciler.
    
17. **Catalog concurrency invariants are unenforced.** Publish/cancel/reschedule can pass checks on stale copies and emit contradictory events. Duplicate section price tiers lack a database uniqueness constraint. Evidence: [EventService.cs (line 83)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Catalog/TicketFlow.Catalog.Application/Events/EventService.cs:83), [EventConfiguration.cs (line 9)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Catalog/TicketFlow.Catalog.Infrastructure/Configurations/EventConfiguration.cs:9).  
    **Solution:** Add optimistic concurrency/version tokens and schema-level unique constraints; return conflicts instead of last-write-wins.
    
18. **Search and Notification can resurrect cancelled events.** Published, rescheduled, and cancelled events use separate queues and contain no aggregate revision. Late publication overwrites newer cancellation/schedule state. Elasticsearch write failures are also ignored and acknowledged. Evidence: [MongoEventReminderReadModel.cs (line 39)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Notification/TicketFlow.Notification.Infrastructure/MongoEventReminderReadModel.cs:39), [ElasticEventSearchIndex.cs (line 16)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Search/TicketFlow.Search.Infrastructure/ElasticEventSearchIndex.cs:16).  
    **Solution:** Add monotonic aggregate revisions and cancellation tombstones, conditionally apply only newer versions, and throw on invalid Elasticsearch responses.
    
19. **Organizer event creation is a client-side distributed transaction.** Venue, event, seat map, pricing, inventory, and publication are separately committed. Failure leaves partial resources; retry duplicates them. Evidence: [actions.ts (line 40)](D:/code/Projects/roadmap-projects/ticket-flow/apps/organizer-dashboard/src/app/organizer/events/new/actions.ts:40).  
    **Solution:** Move this to a durable, idempotent backend onboarding workflow with a stable creation key, recorded steps, resume, and cleanup.
    
20. **Identity registration has an unreliable Keycloak/Postgres compensation workflow.** Uncertain Keycloak creates cannot always be found, cleanup reuses a cancelled request token, and delete failures leave divergent systems. Registration also marks arbitrary email addresses verified without proof. Evidence: [KeycloakAdminClient.cs (line 25)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Identity/TicketFlow.Identity.Infrastructure/Keycloak/KeycloakAdminClient.cs:25), [UserService.cs (line 15)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Identity/TicketFlow.Identity.Application/Users/UserService.cs:15).  
    **Solution:** Add a durable registration state machine and stable key, independent reconciliation/cleanup, email verification, password policy, and rate limiting.
    
21. **Recommendation’s first-purchase upsert can create duplicate user histories.** Mongo upserts by `UserId`, but `_id` is unrelated and there is no unique `UserId` index. Evidence: [MongoPurchaseHistoryStore.cs (line 13)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Recommendation/TicketFlow.Recommendation.Infrastructure/MongoPurchaseHistoryStore.cs:13).  
    **Solution:** Use `UserId` as `_id` or add a unique index and retry duplicate-key races.
    
22. **Live seat state can become incorrect in the browser.** Hold conflicts are thrown before the intended conflict result is handled; selected seats invalidated by SignalR remain selected and counted. Reconnect does not rejoin the event group or fetch a snapshot. Evidence: [actions.ts (line 22)](D:/code/Projects/roadmap-projects/ticket-flow/apps/customer-web/src/app/events/[id]/actions.ts:22), [SeatPicker.tsx (line 51)](D:/code/Projects/roadmap-projects/ticket-flow/apps/customer-web/src/app/events/[id]/SeatPicker.tsx:51), [useSeatMapLive.ts (line 20)](D:/code/Projects/roadmap-projects/ticket-flow/apps/customer-web/src/app/events/[id]/useSeatMapLive.ts:20).  
    **Solution:** Use discriminated results for conflicts, remove invalid selections immediately, and rejoin plus reconcile a full snapshot after reconnect.
    
23. **Duplicate checkout submission has no end-to-end idempotency.** Double-clicks or retries can create two orders for one hold, with the browser potentially following the cancelled one.  
    **Solution:** Add a server-enforced idempotency key and unique hold mapping; UI pending-state prevention is secondary defense.
    
24. **The scanner multi-zone route is broken.** Its relative `/api/validate` call targets the customer shell when mounted under `/scanner`, while rewrites cover only `/scanner/*`; manifest paths are also rooted incorrectly. Evidence: [ScannerClient.tsx (line 73)](D:/code/Projects/roadmap-projects/ticket-flow/apps/gate-scanner/src/app/scanner/ScannerClient.tsx:73), [next.config.ts (line 31)](D:/code/Projects/roadmap-projects/ticket-flow/apps/customer-web/next.config.ts:31).  
    **Solution:** Make scanner API/assets zone-scoped and set manifest `scope` and `start_url` to `/scanner`.
    
25. **Provider access tokens are exposed through Auth.js session objects.** Refresh failure and rotation races are not coordinated, and the three localhost apps namespace only part of their OAuth cookie state. Evidence: [customer auth.ts (line 79)](D:/code/Projects/roadmap-projects/ticket-flow/apps/customer-web/src/auth.ts:79).  
    **Solution:** Keep provider tokens server-only, centralize single-flight refresh with forced reauthentication on failure, and namespace all OAuth cookies or use separate hosts.
    
26. **Media endpoints permit memory/decompression denial of service.** Gateway and Lambda buffer/base64-decode entire uploads without request, image-dimension, or pixel limits. Evidence: [MediaEndpoints.cs (line 33)](D:/code/Projects/roadmap-projects/ticket-flow/src/Gateway/TicketFlow.Gateway/MediaEndpoints.cs:33), [ThumbnailPosterFunction.cs (line 29)](D:/code/Projects/roadmap-projects/ticket-flow/src/Functions/TicketFlow.Functions/ThumbnailPosterFunction.cs:29).  
    **Solution:** Enforce body and decoded-pixel limits, stream/direct-upload objects, validate formats, and cap thumbnail dimensions.
    
27. **Notification delivery is not reliable.** Reminders are marked sent before delivery; current order completion handling only logs rather than sending. Evidence: [SendUpcomingEventRemindersConsumer.cs (line 31)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Notification/TicketFlow.Notification.Api/Consumers/SendUpcomingEventRemindersConsumer.cs:31), [NotifyOrderCompletedConsumer.cs (line 30)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Notification/TicketFlow.Notification.Api/Consumers/NotifyOrderCompletedConsumer.cs:30).  
    **Solution:** Introduce delivery attempts/outbox records, provider idempotency, and mark delivered only after provider confirmation.
    

## P2 — correctness and operational quality

28. **Time zones are handled implicitly.** `datetime-local` values are parsed in the server’s timezone, so production UTC can shift venue-local event times. Evidence: [NewEventForm.tsx (line 91)](D:/code/Projects/roadmap-projects/ticket-flow/apps/organizer-dashboard/src/app/organizer/events/new/NewEventForm.tsx:91).  
    **Solution:** Submit an explicit IANA zone/offset, store UTC plus event zone, and render in the venue/user timezone.
    
29. **Polling and API contracts are fragile.** Async `setInterval` calls overlap, stale responses can regress the UI, ticket fetches do not consistently check `res.ok`, and handwritten TypeScript types already disagree with hold-conflict behavior. Evidence: [OrderStatus.tsx (line 15)](D:/code/Projects/roadmap-projects/ticket-flow/apps/customer-web/src/app/orders/[id]/OrderStatus.tsx:15), [types (line 73)](D:/code/Projects/roadmap-projects/ticket-flow/packages/types/src/index.ts:73).  
    **Solution:** Use abortable sequential polling/backoff and generate runtime-validated contracts from API schemas.
    
30. **Reservation outages are displayed as “sold out.”** Catalog catches availability failures and returns an empty list, making infrastructure failure indistinguishable from zero inventory. Evidence: [HttpSeatAvailabilityClient.cs (line 17)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Catalog/TicketFlow.Catalog.Infrastructure/EventDetails/HttpSeatAvailabilityClient.cs:17).  
    **Solution:** Return explicit degraded/unavailable metadata and block checkout without misreporting inventory.
    
31. **Automatic CI does not run on the real default branch.** The workflow watches `master`, but the repository uses `main`. There is also no frontend test suite or explicit frontend test job. Evidence: [ci.yml (line 3)](D:/code/Projects/roadmap-projects/ticket-flow/.github/workflows/ci.yml:3).  
    **Solution:** Trigger on `main`, add frozen installs, lint/type checking, unit tests, and multi-zone Playwright purchase/scanner tests.
    
32. **The Kubernetes smoke environment is incomplete and readiness is shallow.** Services reference host LocalStack that CI does not deploy, migrations depend on runtime environment behavior, and basic `/health` can report ready while required databases/functions/object storage are unavailable.  
    **Solution:** Deploy all dependencies in CI, move schema changes to migration jobs, and split liveness from tagged dependency readiness.
    
33. **Deployment configuration is development-grade.** Kubernetes uses the `Docker` environment, tracked realm data contains seeded users/client credentials, transport is HTTP, and service/broker credentials are broad. Kafka topic creation is also a one-shot startup race, and several images float on `latest`. Evidence: [services.yaml (line 62)](D:/code/Projects/roadmap-projects/ticket-flow/infrastructure/k8s/ticketflow/templates/services.yaml:62), [KafkaTopicSetup.cs (line 16)](D:/code/Projects/roadmap-projects/ticket-flow/src/Services/Analytics/TicketFlow.Analytics.Api/Messaging/KafkaTopicSetup.cs:16).  
    **Solution:** Create a distinct production configuration, externalize/rotate secrets, add TLS/network policies and least-privilege broker accounts, pin images, and provision topics through infrastructure or retrying startup logic.
    

## Implementation plan

1. **Build failure-boundary tests first.** Add tampered checkout tests, duplicate business-key delivery, crash injection after every write/publish/provider call, milestone permutations against real RabbitMQ/Marten, concurrent payments/scans, and Redis/broker restart tests.
    
2. **Establish canonical identifiers and ownership.** Add checkout idempotency keys, hold/order/allocation ownership, seat generations, customer/event/organizer ownership, currency, catalog revisions, and optimistic concurrency columns/constraints.
    
3. **Repair money and seat integrity.** Replace client pricing with server pricing, implement the durable Reservation allocation ledger and receipts, conditional compensation, payment attempt/refund states, provider idempotency, and reconciliation.
    
4. **Make Ordering recoverable.** Add transactional initiation/outbox, one ordered writer per order, inbox deduplication, expected versions, saga timeouts, redrive tooling, and a canonical completion event. Remove Kafka and notifications from the critical path.
    
5. **Repair Ticketing and authorization.** Add durable issuance jobs, atomic event-scoped check-in, revocation, customer IDOR protection, organizer tenancy, gate assignments, and service-level Reservation authorization.
    
6. **Version projections and distributed workflows.** Make Search/Notification revision-aware, check Elasticsearch responses, and convert event onboarding and identity registration into resumable workflows.
    
7. **Correct client behavior.** Fix hold conflicts, reconnect reconciliation, checkout submission state, scanner routing, server-only tokens, timezone handling, polling, and degraded-state reporting.
    
8. **Harden deployment and release gates.** Persist all stateful infrastructure, fix the CI branch, deploy complete test dependencies, add migration/readiness jobs, secure production configuration, and require concurrency/fault/restart tests before release.
    

The highest-risk release sequence is therefore: **seat/payment integrity → durable ordering saga → ticket/auth controls → versioned projections → clients → infrastructure hardening**.
