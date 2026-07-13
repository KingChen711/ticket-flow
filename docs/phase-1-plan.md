---
title: "Phase 1 — Domain modeling & CRUD services — Kế hoạch commit"
phase: 1
chapters: [Ch2 DDD, Ch3 Synchronous communication, Ch8 Database strategies]
created: 2026-07-13
tags: [ticketflow, phase-1, plan, ddd, grpc, efcore]
---

# Phase 1 — Kế hoạch chi tiết theo commit

> **Mục tiêu (từ scope mục 10):** DDD tactical patterns + giao tiếp sync (gRPC) + DB-per-service.
> **DoD:** CRUD sự kiện qua gateway; mỗi service DB riêng; 1 lời gọi gRPC nội bộ chạy được.

## Quyết định kiến trúc cho Phase 1

- **Layering:** `catalog` và `identity` tách 4 project theo Clean Architecture (`Domain` → `Application` → `Infrastructure` → `Api`), ép đúng dependency rule để học Ch2. `ordering` và `reservation` **chỉ là skeleton** (1 project `Api`), sẽ mổ xẻ đầy đủ ở Phase 4/5 — giờ chỉ dựng đủ để luyện gRPC (Ch3).
- **DB-per-service (Ch8):** mỗi service 1 **database riêng** trên cùng Postgres container: `ticketflow_catalog`, `ticketflow_identity`. Npgsql `Database.Migrate()` tự tạo DB nếu chưa có → không cần init script.
- **Domain events (Ch2):** Phase 1 chỉ **raise nội bộ** vào `AggregateRoot.DomainEvents`, CHƯA publish. Outbox + message bus là Phase 3.
- **CQRS/MediatR:** CHƯA dùng ở Phase 1 (đó là Phase 4). CRUD đi qua service class + repository trực tiếp để giữ commit nhỏ, tránh gold-plating.
- **Auth:** dùng **fake JWT** (ký bằng symmetric key, dev-only). OAuth2/OIDC thật là Phase 7 (Ch13).
- **Ports:** theo scope mục 4 — catalog 8002, identity 8001, reservation 8003/gRPC 9003, ordering 8004/gRPC 9004.

---

## Commit 0 — Baseline Phase 0 (bắt buộc làm trước)

Working tree đang chứa toàn bộ Phase 0 chưa commit. Chốt lại làm mốc:

```
chore: scaffold Phase 0 skeleton — BuildingBlocks, catalog, YARP gateway, docker compose

- TicketFlow.Domain (Entity, AggregateRoot, DomainEvent, Result)
- TicketFlow.Web (exception middleware, health check, swagger)
- Catalog.Api (/hello, /health, swagger)
- Gateway (YARP -> catalog)
- docker-compose.infra.yml (postgres/redis/rabbitmq/kafka) + root compose
```

> Sau commit này có thể tạo nhánh `phase-1` để làm các commit dưới.

---

## Nhóm A — Catalog domain & CRUD (lõi của DoD)

### Commit 1 — `feat(domain): add ValueObject base + guard clause to BuildingBlocks`
- `TicketFlow.Domain/ValueObject.cs`: base class DDD (`GetEqualityComponents()` + structural equality).
- `TicketFlow.Domain/Ensure.cs` (hoặc `Guard`): helper `NotNullOrEmpty`, `NotNegative`, `Range`... để aggregate tự bảo vệ invariant.
- **Ch2:** Value Object, invariant enforcement.
- *Build green:* chỉ thêm vào classlib sẵn có.

### Commit 2 — `feat(catalog): model Venue aggregate (DDD tactical patterns)`
- Tạo project `TicketFlow.Catalog.Domain` (classlib, ref `TicketFlow.Domain`).
- `Venue : AggregateRoot<Guid>` { `Name`, `Address` (VO), `SeatMap` }.
- `Address` VO (Street, City, Country, PostalCode).
- `SeatMap` (owned) chứa `Section : Entity<Guid>` { `Name`, `Capacity` }.
- Factory `Venue.Register(...)` → raise `VenueRegistered`.
- **Ch2:** Aggregate root, Entity, Value Object, Domain Event, ubiquitous language.

### Commit 3 — `feat(catalog): model Event aggregate with Money & schedule VOs`
- `Money` VO (Amount ≥ 0, Currency), `EventSchedule` VO (StartsAt < EndsAt).
- `Event : AggregateRoot<Guid>` { `Title`, `Description`, `Artist`, `VenueId` (Guid — tham chiếu aggregate khác **bằng id**), `Schedule`, `Status` (Draft/Published/Cancelled), `PriceTiers` }.
- `PriceTier : Entity<Guid>` { `SectionName`, `Price` (Money) }.
- Behavior + guard: `Event.Create()` (raise `EventCreated`), `Publish()` (chặn nếu chưa có price tier / lịch trong quá khứ → raise `EventPublished`), `Reschedule()` (`EventRescheduled`), `Cancel()` (`EventCancelled`).
- **Ch2:** aggregate boundary, reference-by-id, behavior-rich domain, domain events.

### Commit 4 — `test(catalog): unit tests for Event & Venue invariants`
- Project `TicketFlow.Catalog.Domain.Tests` (xUnit).
- Test: publish thiếu price tier → fail; reschedule về quá khứ → fail; Money âm → throw; publish thành công raise đúng event.
- **Scope mục 11:** unit test domain/aggregate.

### Commit 5 — `feat(catalog): application layer (DTOs, repository ports, services)`
- Project `TicketFlow.Catalog.Application` (ref Domain).
- Ports: `IEventRepository`, `IVenueRepository`, `IUnitOfWork`.
- `EventService`, `VenueService` (orchestrate domain + repo).
- Request/Response DTOs + mapping (Create/Update/Publish/Get/List).
- **Ch2 (application boundary):** thin orchestration, domain giữ business rule.

### Commit 6 — `feat(catalog): EF Core + Postgres persistence (Ch8 DB-per-service)`
- Project `TicketFlow.Catalog.Infrastructure` (ref Application; add `Npgsql.EntityFrameworkCore.PostgreSQL`).
- `CatalogDbContext` (schema `catalog`), DbSet<Event>, DbSet<Venue>.
- EF configs: owned types cho `Address`/`Money`/`SeatMap`/`Section`/`PriceTier`; snake_case naming convention.
- Repository implementations + `UnitOfWork`.
- `AddCatalogInfrastructure(config)` DI extension đọc `ConnectionStrings:Default`.
- **Ch8:** DB riêng, polyglot persistence (Postgres), mapping domain ↔ table.

### Commit 7 — `feat(catalog): REST CRUD endpoints wired through layers`
- Viết lại `Catalog.Api/Program.cs`: đăng ký Application + Infrastructure; bỏ `/hello`.
- Endpoints:
  - `POST /venues`, `GET /venues`, `GET /venues/{id}`
  - `POST /events`, `GET /events`, `GET /events/{id}`, `PUT /events/{id}`, `POST /events/{id}/publish`, `POST /events/{id}/cancel`
- Swagger cập nhật; giữ `/health`.
- **Ch3 (REST phía ngoài):** service phơi REST cho gateway.

### Commit 8 — `feat(catalog): initial migration, startup migrate & seed data`
- `dotnet ef migrations add InitialCatalog`.
- Dev-only: `Database.Migrate()` lúc khởi động (tự tạo `ticketflow_catalog`).
- Seeder: 2–3 venue + vài event mẫu (scope Phase 1.5).
- **Ch8:** migration, seed.

### Commit 9 — `feat(catalog): dockerize with Postgres connection`
- Compose: catalog thêm `ConnectionStrings__Default`, `depends_on: postgres (healthy)`.
- Verify: `docker compose up` → CRUD event **qua gateway** (`/catalog/events`) trả 200. ✅ (một nửa DoD)

---

## Nhóm B — Identity service (DB riêng thứ 2 + fake auth)

### Commit 10 — `feat(identity): User aggregate, Email VO, Role enum`
- Project `TicketFlow.Identity.Domain`.
- `Email` VO (validate format). `Role` enum (Customer/Organizer/Admin).
- `User : AggregateRoot<Guid>` { Email, DisplayName, PasswordHash, Roles }; `User.Register(...)` → raise `UserRegistered`.
- **Ch2:** thêm một bounded context với ubiquitous language riêng.

### Commit 11 — `feat(identity): EF Core persistence (own database)`
- `TicketFlow.Identity.Application` (ports + `UserService`) + `TicketFlow.Identity.Infrastructure`.
- `IdentityDbContext` (schema `identity`, DB `ticketflow_identity`), config, repo, migration `InitialIdentity`, startup migrate.
- **Ch8:** DB-per-service — chứng minh 2 service = 2 database tách biệt.

### Commit 12 — `feat(identity): registration, user list & dev JWT token`
- `Identity.Api`: `POST /identity/register`, `GET /identity/users`, `POST /identity/dev-token` (ký JWT symmetric, claim `sub` + `role` — placeholder tới Phase 7).
- Password hashing (PBKDF2/BCrypt).
- Compose: thêm service `identity` (8001) + `ConnectionStrings__Default`.
- Gateway: route `/identity/**` → identity cluster.
- **Ch13 (đặt nền, chưa hoàn thiện):** token, role — chỉ mức giả lập.

---

## Nhóm C — gRPC sync comm (Ch3): ordering ⇄ reservation

### Commit 13 — `feat(contracts): reservation gRPC contract (.proto)`
- `contracts/protos/reservation.proto`: `ReservationService.CheckSeatAvailability(CheckSeatAvailabilityRequest{event_id, seat_ids[]}) → CheckSeatAvailabilityResponse{all_available, unavailable_seat_ids[]}`.
- **Ch3:** Protobuf, contract-first.

### Commit 14 — `feat(reservation): gRPC server skeleton (in-memory seat check)`
- Project `TicketFlow.Reservation.Api` (`Grpc.AspNetCore`), `<Protobuf ... GrpcServices="Server">`.
- Implement `CheckSeatAvailability` trả kết quả **in-memory/hardcoded** (giả định còn chỗ) — logic thật là Phase 2.
- HTTP 8003 (health) + gRPC 9003 (HTTP/2). Thêm vào compose.

### Commit 15 — `feat(ordering): gRPC client skeleton calling reservation`
- Project `TicketFlow.Ordering.Api`, `<Protobuf ... GrpcServices="Client">` cùng .proto.
- Đăng ký gRPC client (`AddGrpcClient<ReservationService.ReservationServiceClient>`) trỏ `http://reservation:9003`.
- REST `POST /ordering/check-seats` → gọi reservation qua gRPC → trả kết quả (chứng minh sync comm end-to-end).
- Compose: service `ordering` (8004/9004). Gateway route `/ordering/**`.
- **Ch3:** sync request/response qua gRPC giữa 2 service.

---

## Commit 16 — `chore: finalize Phase 1 — solution refs, routes, DoD checklist`
- Add tất cả project mới vào `TicketFlow.slnx`.
- Rà lại gateway routes (`/catalog`, `/identity`, `/ordering`) + compose network.
- Tick DoD Phase 1 trong scope.md.

---

## Kiểm chứng DoD cuối Phase 1

| DoD | Kiểm chứng |
|---|---|
| CRUD sự kiện qua gateway | `POST/GET/PUT /catalog/events` qua `:8080` trả 2xx |
| Mỗi service DB riêng | `\l` trên Postgres thấy `ticketflow_catalog` + `ticketflow_identity` tách biệt |
| 1 gRPC nội bộ chạy được | `POST /ordering/check-seats` → ordering gọi reservation qua gRPC → trả availability |

## Thứ tự đọc sách trước khi code (scope mục 13)
Đọc **Ch2 (DDD)** trước Nhóm A/B, **Ch8** trước commit 6, **Ch3** trước Nhóm C.
