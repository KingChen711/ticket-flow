---
title: "Phase 3 — Async messaging & event-driven — Kế hoạch commit"
phase: 3
chapters: [Ch4 Communication patterns, Transactional Outbox, Integration events]
created: 2026-07-13
tags: [ticketflow, phase-3, plan, masstransit, rabbitmq, outbox, event-driven]
---

# Phase 3 — Kế hoạch chi tiết theo commit

> **Mục tiêu (từ scope mục 10):** chuyển từ gọi trực tiếp (REST/gRPC đồng bộ) sang **event-driven** bất đồng bộ. Dựng message bus + Transactional Outbox để event không mất.
> **DoD:** tạo sự kiện ở catalog → event chảy qua bus → notification nhận được; **tắt broker giữa chừng, bật lại → event không mất (Outbox)**.

> ⚠️ **Viết TRƯỚC khi code** (giống [phase-2-plan.md](phase-2-plan.md), khác [phase-1-plan.md](phase-1-plan.md) vốn viết trong lúc code). Các mục "lệch kế hoạch / bug tự bắt được" và "verify thật" sẽ được bổ sung dần khi thực thi từng commit. Version package sẽ được chốt vào `Directory.Packages.props` lúc code (chưa pin sẵn ở đây).

---

## Bối cảnh: cái gì đã có, cái gì Phase 3 thêm

- **Broker đã sẵn từ Phase 0** trong `infrastructure/docker/docker-compose.infra.yml`: RabbitMQ `3-management` (5672 + UI 15672, user/pass `ticketflow`) và Kafka `apache/kafka:3.8.0` (KRaft, listener nội bộ `kafka:9092`, host `localhost:29092`). **Không cần đụng infra.**
- **Comm hiện tại toàn đồng bộ:** ordering ⇄ reservation qua gRPC (Phase 1/2). Chưa có 1 dòng async messaging nào. Phase 3 là lần đầu có bus.
- **Catalog đã có domain event sẵn** (`EventCreated`, `EventPublished`, `EventCancelled`, `EventRescheduled`, `VenueRegistered`, `SeatMapCreated` — tất cả `: DomainEvent`, in-process, đang bị `Ignore(DomainEvents)` ở EF). Đây là **domain event nội bộ**, KHÁC integration event sẽ publish lên bus (xem quyết định kiến trúc — đây là điểm học chính).
- **Phase 3 thêm:** `BuildingBlocks.Messaging`, 1 shared contracts classlib, Outbox cho catalog, và **2 service mới**: `notification-service` + `search-service` (đều là consumer mỏng).

---

## Quyết định kiến trúc cho Phase 3

### 1. Transactional Outbox — trái tim của Phase 3 (điểm học Ch4)
- **Vấn đề dual-write:** "lưu Event vào Postgres" và "publish message lên RabbitMQ" là **2 hệ thống khác nhau**, không có transaction chung. Nếu commit DB xong rồi publish fail (broker chết, mạng đứt) → **mất event**. Nếu publish xong rồi commit DB fail → **event ma** (consumer xử lý thứ chưa từng tồn tại). Đây chính là thứ DoD ép chứng minh.
- **Lời giải Outbox:** ghi message vào bảng `outbox_message` **trong CÙNG transaction** với thay đổi nghiệp vụ (Postgres đảm bảo atomic). Một **delivery service** (background) đọc các row chưa gửi, publish lên bus, đánh dấu đã gửi. Broker chết → message nằm yên trong Postgres → broker sống lại → drain tiếp. **Không mất.**
- **Dùng MassTransit EF Core Outbox** (`AddEntityFrameworkOutbox<CatalogDbContext>`): nó tạo sẵn bảng `outbox_message`, `outbox_state`, `inbox_state` và bus/delivery service — không tự tay viết outbox loop. Đây là lý do chọn MassTransit thay vì `IPublishEndpoint` trần của transport.
- **Chỉ PUBLISHER cần outbox** (catalog). Phía consumer, độ bền do **RabbitMQ tự lo** (queue có `durable`, message `persistent`) + **inbox dedup** cho idempotency (xem mục 4).

### 2. Domain event ≠ Integration event — và va chạm tên `EventPublished` (điểm học DDD)
- **Domain event** (`EventPublished : DomainEvent` đã có trong catalog) = việc-đã-xảy-ra **bên trong** bounded context catalog, hình dạng gắn với aggregate nội bộ, tiêu thụ **in-process**. **KHÔNG được** ném thẳng lên bus — làm vậy là rò rỉ mô hình nội bộ, khoá cứng consumer vào aggregate của catalog (đổi domain là vỡ mọi service khác).
- **Integration event** = **hợp đồng cross-service ổn định**, publish lên bus, do các service khác tiêu thụ. Là lớp **chống hủ hoá (anti-corruption)**: domain event → (handler trong catalog dịch) → integration event ổn định → publish qua outbox.
- **Va chạm tên có thật:** scope gọi integration event là `EventPublished`, nhưng catalog **đã có** domain event trùng tên y hệt. → Integration contract đặt namespace/tên phân biệt rõ, ví dụ `TicketFlow.Contracts.Messaging.Catalog.CatalogEventPublished` (hoặc `...IntegrationEvent` hậu tố). MassTransit route theo **full type name (namespace + tên)** nên namespace tách là đủ về mặt kỹ thuật, nhưng vẫn đặt tên khác để người đọc không lẫn. Sẽ chốt tên cụ thể ở Commit 2.
- **Contract chứa dữ liệu đủ để consumer tự chủ**, không bắt consumer gọi ngược lại catalog: `CatalogEventId`, `Title`, `VenueId`, `StartsAt`... (đủ để notification soạn nội dung, search index sơ bộ). Không nhét cả aggregate.

### 3. Message contract sống ở shared classlib (khác kiểu gRPC)
- gRPC ở dự án này chia sẻ **file `.proto`** (`contracts/protos/reservation.proto`), mỗi Api tự compile — KHÔNG có assembly chung. Với MassTransit, contract là **kiểu C#**; cách sạch nhất trong monorepo là **1 classlib chung** `TicketFlow.Contracts.Messaging` được publisher + consumer cùng reference (tránh mỗi service tự khai lại 1 bản record dễ lệch).
- **Consumer (notification/search) KHÔNG reference `Catalog.Domain`** — chỉ reference `Contracts.Messaging`. Giữ tính tự chủ của service: notification không cần biết Event aggregate của catalog trông thế nào.

### 4. Idempotency + Inbox cho consumer
- Bus giao hàng **at-least-once** → 1 message có thể tới **2 lần** (retry, redelivery sau crash). Consumer **phải idempotent**: xử lý lần 2 không được sinh hệ quả kép.
- Phase 3 consumer chỉ **log** ("sẽ gửi" / "sẽ index") nên tự nhiên idempotent — chưa cần inbox thật. Ghi rõ: **khi consumer bắt đầu ghi state** (Phase 6 search index thật, notification lưu lịch sử gửi) mới bật **MassTransit inbox** (`AddEntityFrameworkOutbox` phía consumer) để dedup. Chưa vàng hoá ở Phase 3.

### 5. RabbitMQ là workhorse Phase 3 — Kafka Rider HOÃN sang Phase 6 (phản biện scope)
- Scope Phase 3 liệt kê *"MassTransit (RabbitMQ) **+ Kafka Rider**"*. Tôi **phản biện**: Phase 3 chỉ cần **RabbitMQ**.
  - **RabbitMQ** = pub/sub + routing + per-message ack + retry/DLQ → đúng ngữ nghĩa "một việc nghiệp vụ vừa xảy ra, fan-out cho vài consumer" (catalog → notification + search). DoD hoàn toàn giải quyết bằng RabbitMQ + Outbox.
  - **Kafka** = event **log** bền, ordered, partitioned, **replayable** → đúng cho **stream phân tích / event feed thông lượng cao**. Ở dự án này Kafka mới thực sự cần ở **Phase 6** (`analytics-service` consume stream doanh số → ClickHouse) và hỗ trợ tư duy replay của **Phase 4** (event sourcing).
  - Thêm Kafka Rider ngay Phase 3 khi **chưa có consumer nào cần ngữ nghĩa stream** là scope creep (đúng kỷ luật YAGNI đã giữ ở Phase 1/2: không thêm gRPC catalog→reservation, không nâng Redis lên BuildingBlocks khi chưa có consumer thứ 2).
- **Quyết định:** Phase 3 dựng `BuildingBlocks.Messaging` với **RabbitMQ transport**, thiết kế API `AddTicketFlowMessaging` **để thêm Kafka Rider về sau không phá vỡ** (bus abstraction sẵn sàng). Kafka infra vẫn để nguyên (đã chạy). → **✅ Đã duyệt (Commit 8):** `scope.md` Phase 3 sửa lại bỏ "Kafka Rider", Phase 6 ghi chú sẽ bật ở đó.

### 6. MassTransit v8 (miễn phí) — KHÔNG nhảy v9 (thương mại)
- **MassTransit v9 chuyển sang giấy phép thương mại** (trả phí). **v8 là bản OSS cuối** và vẫn được vá. Với dự án học, **pin v8** (dòng `8.x` mới nhất tương thích `net10.0`) — ghi chú rõ lý do trong `Directory.Packages.props` (giống cách đã ghi chú EF Core / OpenAPI). Version cụ thể xác minh khi code Commit 1 (kiểm tra hỗ trợ net10.0). Đây là **quyết định phụ thuộc có ý thức**, không dính bẫy nâng đại lên bản mới nhất.

### 7. Notification & Search là service CONSUMER mỏng — không full 4 tầng, không lên gateway
- Hai service mới **không** cần Clean Architecture 4 tầng như catalog. Bản chất Phase 3 là **1 host + consumer class + wiring MassTransit**. Giống tinh thần "domain mỏng" của reservation: không gượng ép tầng cho thứ chưa có nghiệp vụ.
- **Host tối thiểu ASP.NET** (không phải Worker trần) để đồng bộ câu chuyện **health check** với phần còn lại (bus health + `/health`) và để sẵn chỗ cắm HTTP sau này. MassTransit hosted service chạy tốt trong host này.
- **Chưa route qua gateway:** notification không có public API; search **sẽ** có endpoint truy vấn ở **Phase 6** (lúc đó mới thêm `search-route`). Phase 3 để 2 service này ngoài gateway.
- **Port mới:** notification `8005`, search `8006` (nối tiếp dãy hiện có: catalog 8002, identity 8001, reservation 8003/9003, ordering 8004). Chỉ expose HTTP cho health, chưa cần cổng gRPC.

### 8. Kế thừa & hạ tầng
- **Bus health check** cắm vào pipeline health sẵn có (`TicketFlow.Web`) — mỗi service có bus báo cáo trạng thái kết nối RabbitMQ.
- **Naming:** endpoint/queue kebab-case (MassTransit default `KebabCaseEndpointNameFormatter`), 1 queue / 1 consumer; error đi vào `_error`, dead-letter `_skipped` (mặc định MassTransit) — verify được trên UI 15672.
- **Retry:** cấu hình retry ngắn (vd 3 lần, backoff) + redelivery ở `AddTicketFlowMessaging`, mặc định chung cho mọi consumer.
- **Kế thừa Phase 1/2:** Central Package Management, native OpenAPI + Scalar (cho service có HTTP), EF Core snake_case + `Ignore(DomainEvents)` + ctor rỗng, `Guard`/`ValueObject`, exception middleware, migrate-on-startup dev-only, cập nhật `.slnx` + Dockerfile mỗi khi thêm project.

---

## Nhóm A — Nền messaging (BuildingBlocks + contracts)

### Commit 1 — `feat(messaging): BuildingBlocks.Messaging — MassTransit + RabbitMQ`
- Project mới `src/BuildingBlocks/TicketFlow.Messaging` (classlib). Pin MassTransit v8 + `MassTransit.RabbitMQ` vào `Directory.Packages.props`.
- Extension `AddTicketFlowMessaging(IServiceCollection, IConfiguration, Action<IBusRegistrationConfigurator>? configure)`: cấu hình RabbitMQ host (từ config `RabbitMq:Host/Username/Password`), `KebabCaseEndpointNameFormatter`, retry mặc định (3 lần / 2s), `ConfigureEndpoints`. ⚠️ **Chữ ký này đã đổi ở Commit 6** — thêm tham số bắt buộc `serviceName` (xem ghi chú Commit 6) sau khi phát hiện bug đặt trùng tên queue giữa 2 service. Coi mô tả dưới đây là lịch sử tại thời điểm Commit 1, không phải API hiện tại.
- **Lệch kế hoạch:** plan gốc định "hand-roll bus health check vào `TicketFlow.Web`" (theo mẫu `RedisHealthCheck`). Thực tế **không cần** — `AddMassTransit(...)` tự đăng ký `IHealthCheck` tên `masstransit-bus` vào DI, tự surface qua `/health` miễn `services.AddHealthChecks()` đã được gọi trước đó (đã có sẵn trong `AddTicketFlowWeb()`). Khác Redis (Phase 2) — StackExchange.Redis không có tích hợp ASP.NET Core sẵn nên phải viết tay; MassTransit có, nên **không viết tay để tránh trùng lặp**. `TicketFlow.Messaging` không cần reference `TicketFlow.Web`.
- Package version chốt: `MassTransit`/`MassTransit.RabbitMQ` **8.5.10** — xác nhận qua nuspec (`curl` NuGet flat-container API) đây là bản 8.x mới nhất **và có target `net10.0` riêng** (không chỉ fallback `netstandard2.0`). NuGet mặc định trả `MassTransit` **9.1.2** khi search — đúng như quyết định kiến trúc mục 6: 9.x đã sang giấy phép thương mại, chủ động pin xuống 8.x.
- **Wiring tạm ở catalog** (`Program.cs`): `AddTicketFlowMessaging(builder.Configuration)` không kèm consumer nào — chỉ để chứng minh building block tự nó chạy được trước khi Commit 3-4 gắn Outbox. Đặt trong `Program.cs`, chưa vào `AddCatalogInfrastructure`, vì Commit 3 nhiều khả năng dời vào đó khi Outbox cần chung 1 `AddMassTransit` call với `CatalogDbContext`.
- Thêm `RabbitMq:Host/Username/Password` vào `appsettings.json` (Docker: host `rabbitmq`) và `appsettings.Development.json` (local: host `localhost`) của catalog — theo đúng pattern 2-file đã dùng cho Redis ở Reservation. `docker-compose.yml`: catalog thêm `depends_on: rabbitmq (healthy)`. Dockerfile: thêm dòng COPY csproj riêng cho `TicketFlow.Messaging` vào layer restore-cache (bị thiếu ban đầu — build vẫn qua vì layer COPY toàn bộ `BuildingBlocks/` phía sau vẫn kéo được, nhưng phá cache-invalidation đúng nghĩa của layer restore; sửa lại cho nhất quán với các BuildingBlocks khác).
- **Verify thật (không mock):**
  1. Chạy catalog local (`dotnet run`, `ASPNETCORE_ENVIRONMENT=Development`) nhắm RabbitMQ thật ở `localhost:5672` → log `Bus started: rabbitmq://localhost/`; `/health` → `200 Healthy`.
  2. Gắn tạm `/health-debug` (response writer JSON chi tiết từng check, xoá sau khi xong) để quan sát đúng tên/trạng thái check, không đoán qua chuỗi tổng hợp.
  3. **Phát hiện thật đáng ghi lại:** health check phân biệt 2 tình huống khác nhau —
     - **Broker đã tắt SẴN lúc app start:** health lập tức `Unhealthy 503`, `description: "Not ready: not started"` — đúng như kỳ vọng.
     - **Broker tắt GIỮA CHỪNG sau khi bus đã "started" thành công:** health **vẫn báo `Healthy 200`** trong >13s quan sát được — MassTransit's readiness check phản ánh "bus đã start thành công" chứ không phải "đang có kết nối sống ngay lúc này"; `RabbitMQ.Client` tự động reconnect nền, health không tức thời phản ánh việc mất kết nối tạm thời.
     - **Phục hồi:** dừng hẳn rồi khởi động lại process trong lúc broker tắt → `Unhealthy 503` → bật broker lại → tự chuyển `Healthy 200` sau vài giây, **không cần restart app**.
     - **Hệ quả cho Commit 7:** kịch bản "tắt broker giữa chừng" của DoD nên verify bằng **outbox row + log consumer**, không dựa vào `/health` để phát hiện broker chết (health không nhạy với mất-kết-nối-giữa-chừng theo quan sát này).
  4. Rebuild Docker image thật (`docker compose build catalog`) → `docker compose up -d catalog` → log container xác nhận `Bus started: rabbitmq://rabbitmq/` (đúng hostname mạng Docker, không phải `localhost`).
  5. RabbitMQ Management API (`GET /api/connections`, `curl -u ticketflow:ticketflow`) → thấy connection thật từ catalog, `state: running`.
  6. Full smoke test qua gateway: catalog/identity/ordering/reservation đều vẫn `200` — không regression từ việc thêm messaging vào catalog.
  7. `dotnet build` toàn solution: 0 Warning, 0 Error.

### Commit 2 — `feat(contracts): integration event CatalogEventPublished`
- Project mới `src/BuildingBlocks/TicketFlow.Contracts.Messaging` (classlib, không phụ thuộc gì ngoài BCL — xác nhận csproj scaffold mặc định đã đúng, không cần thêm `PackageReference` nào).
- Định nghĩa contract `Catalog/CatalogEventPublished.cs` — `sealed record CatalogEventPublished(Guid CatalogEventId, string Title, Guid VenueId, DateTimeOffset StartsAt, DateTimeOffset PublishedAt)`. Đặt dưới namespace/thư mục `Catalog/` (không phải root) — chừa chỗ cho contract của service khác sau này (`Ordering/`, ...) mà không lẫn.
- **Ghi rõ trong code:** đây là **integration contract ổn định**, đổi = versioning cross-service; KHÔNG import kiểu domain của catalog. Cũng ghi rõ đây KHÔNG phải domain event trùng tên (`TicketFlow.Catalog.Domain.Events.EventPublished`) — domain event đó chỉ mang `CatalogEventId` (id thuần), tiêu thụ in-process; integration event này mang đủ dữ liệu hiển thị cho consumer.
- **Đối chiếu domain event thật** (đọc `Event.cs`/`EventPublished.cs` trước khi viết contract): `Event.Publish(now)` hiện chỉ `Raise(new EventPublished(Id))` — domain event **không** mang `Title`/`VenueId`/`StartsAt`. Nghĩa là **Commit 4** (handler dịch domain → integration event) sẽ phải tự load lại `Event` aggregate (qua `IEventRepository`, đã có sẵn) để lấy các field còn thiếu trước khi publish — ghi chú trước ở đây để Commit 4 không bất ngờ.
- **Verify thật:** build project (0 Warning/0 Error) + build toàn solution; viết scratch console app (throwaway, đã xoá) reference thẳng `TicketFlow.Contracts.Messaging`, serialize/deserialize `CatalogEventPublished` qua `System.Text.Json` (đúng serializer MassTransit dùng mặc định) → round-trip giữ nguyên giá trị (record value equality) — xác nhận contract "sạch" để đi qua wire, không có surprise từ `DateTimeOffset`/`Guid` serialize.

---

## Nhóm B — Publisher + Outbox (catalog)

### Commit 3 — `feat(catalog): EF Core Transactional Outbox`
- Pin `MassTransit.EntityFrameworkCore` **8.5.10** (đồng bộ version với `MassTransit`/`MassTransit.RabbitMQ` — xác nhận qua NuGet flat-container API, cùng dòng 8.x).
- **Lệch kế hoạch (chốt lại chỗ Commit 1 đã dự đoán):** wiring `AddTicketFlowMessaging` dời hẳn từ `Program.cs` vào `AddCatalogInfrastructure` — bắt buộc, vì `AddEntityFrameworkOutbox<CatalogDbContext>()` phải nằm **cùng 1 lời gọi `AddMassTransit`** và cần biết kiểu `CatalogDbContext` (sống ở Infrastructure). Đúng điểm mở rộng đã thiết kế sẵn ở Commit 1 (`Action<IBusRegistrationConfigurator>? configure`) — không phải sửa lại API, chỉ truyền callback thật vào.
- `CatalogDbContext.OnModelCreating`: thêm `modelBuilder.AddInboxStateEntity()/.AddOutboxMessageEntity()/.AddOutboxStateEntity()` — outbox **không tự inject** vào DbContext tuỳ ý, phải khai báo tay. Cả 3 bảng đi vào schema `catalog` (kế thừa `HasDefaultSchema`, không phụ thuộc thứ tự gọi).
- **Bug thật gặp & sửa (va chạm tên y hệt pattern Reservation Commit 8):** `CS0104` — `Event` ambiguous giữa `TicketFlow.Catalog.Domain.Events.Event` và `MassTransit.Event` (MassTransit có sẵn type `Event` cho mục đích khác). Sửa bằng type alias `using Event = TicketFlow.Catalog.Domain.Events.Event;` thay vì `using TicketFlow.Catalog.Domain.Events;` — cùng convention đã lập (`using ApplicationReservationService = ...`).
- Migration `CatalogOutbox` (`dotnet ef migrations add`, EFCore.NamingConventions tự chuyển snake_case cho cả bảng của MassTransit — không cần cấu hình thêm) sinh đúng 3 bảng: `inbox_state`, `outbox_state`, `outbox_message` (có FK giữa `outbox_message` ↔ `outbox_state`/`inbox_state`) + index (`enqueue_time`, `expiration_time`, `created`...).
- `Catalog.Api.csproj`: bỏ `ProjectReference` trực tiếp tới `TicketFlow.Messaging` (đã chuyển sang Infrastructure) — tránh reference thừa từ Commit 1.
- **Sự cố hạ tầng gặp phải:** Docker Desktop rớt hoàn toàn (`docker info` fail, không process nào chạy) giữa lúc rebuild image — phải tự khởi động lại (`Start-Process "Docker Desktop.exe"`, chờ `docker info` thành công). Restart Docker Desktop = **mất sạch container cũ** (không chỉ daemon reconnect) → phải `docker compose up -d` lại **toàn bộ stack** (9 container), không chỉ riêng catalog.
- **Verify thật (không mock):**
  1. `dotnet build` toàn solution: 0 Warning, 0 Error.
  2. `dotnet ef migrations add CatalogOutbox` chạy sạch; migration file review tay xác nhận đúng schema `catalog`, đúng 3 bảng.
  3. Rebuild image thật (`docker compose build catalog`) → `docker compose up -d` toàn stack → log container catalog xác nhận theo đúng thứ tự: migration `CatalogOutbox` được ghi vào `__EFMigrationsHistory` → `Bus started: rabbitmq://rabbitmq/` → **outbox delivery service đang polling thật** (`SELECT ... FROM "catalog"."outbox_state" ... FOR UPDATE SKIP LOCKED`, dọn `inbox_state` hết hạn) — chứng minh hosted service chạy nền, không phải suy đoán từ code.
  4. `psql` trực tiếp vào container (`docker exec ticketflow-postgres psql -U ticketflow -d ticketflow_catalog -c "\dt catalog.*"`) → thấy đủ `inbox_state`, `outbox_message`, `outbox_state` cạnh các bảng nghiệp vụ.
  5. Full smoke test qua gateway: catalog/identity/ordering/reservation đều `200`; `catalog /health` → `200 Healthy`.
  6. Regression: `TicketFlow.Catalog.Domain.Tests` 35/35 pass; `TicketFlow.Reservation.IntegrationTests` 5/5 pass.

### Commit 4 — `feat(catalog): dịch domain → publish CatalogEventPublished qua outbox`
- **Quyết định kiến trúc (không có trong plan gốc, thêm khi code):** thay vì dịch domain event ngay bên trong `Event.Publish()` hay ở tầng Infrastructure, tạo port mới `IIntegrationEventPublisher` (`Abstractions/IIntegrationEventPublisher.cs`, generic `PublishAsync<T>`) trong **Catalog.Application**, implement `MassTransitIntegrationEventPublisher` (wrap `IPublishEndpoint`) ở **Catalog.Infrastructure**. Lý do: đúng convention port/adapter đã lập từ Phase 1/2 (`ISeatHoldStore`, `IUnitOfWork`...) — Application biết **publish cái gì** (kiểu contract, thuần data), Infrastructure biết **publish bằng gì** (MassTransit cụ thể); Application không reference package MassTransit.
- `EventService.PublishAsync`: sau `@event.Publish(now)`, tự tay dựng `new CatalogEventPublished(@event.Id, @event.Title, @event.VenueId, @event.Schedule.StartsAt, now)` (đúng dữ liệu còn thiếu đã cảnh báo ở Commit 2 — lấy trực tiếp từ aggregate đang có sẵn trong scope, không cần load lại) rồi gọi `integrationEventPublisher.PublishAsync(...)` **TRƯỚC** `unitOfWork.SaveChangesAsync()` — thứ tự bắt buộc: `Publish()` chỉ buffer message vào scope của `CatalogDbContext` hiện tại, `SaveChangesAsync()` mới thật sự flush vào `outbox_message` cùng transaction.
- `TicketFlow.Catalog.Application` thêm `ProjectReference` tới `TicketFlow.Contracts.Messaging` (chỉ để dùng kiểu `CatalogEventPublished` — thuần data, không kéo theo MassTransit).
- **Verify thật (không mock), toàn bộ qua gateway thật, không gọi thẳng service):**
  1. Tạo **event mới hoàn toàn** qua đúng luồng API thật: `POST /catalog/events` → `POST /catalog/events/{id}/seatmap` → `POST /catalog/events/{id}/seatmap/sections` → `POST /catalog/events/{id}/price-tiers` → `POST /catalog/events/{id}/publish`. Chọn cách này thay vì tái dùng data cũ vì cả 2 event `Draft` có sẵn trong DB **không có seat map/price tier** — buộc phải đi full luồng, tình cờ lại test được toàn bộ chuỗi nghiệp vụ chứ không chỉ riêng publish.
  2. **Bug thật gặp khi test (không phải bug code Commit 4):** `POST /events` đầu tiên trả `500` — Npgsql ném `ArgumentException: Cannot write DateTimeOffset with Offset=07:00:00 to PostgreSQL type 'timestamp with time zone', only offset 0 (UTC) is supported`. Do payload test dùng giờ VN (`+07:00`) thay vì UTC — Npgsql v6+ bắt buộc `DateTimeOffset` ghi vào `timestamptz` phải là UTC. Sửa payload test dùng `Z`, không phải sửa code (hành vi Npgsql đúng, ứng dụng chưa có API nào expose để test được non-UTC input trước đây trong session này).
  3. Publish trả `200`, `status: "Published"`.
  4. **Phát hiện thật quan trọng — outbox row TRANSIENT, không tồn tại lâu để `psql` bắt kịp:** log container catalog cho thấy full chu trình **trong cùng 1 lần poll**: `INSERT INTO catalog.outbox_state` → `INSERT INTO catalog.outbox_message` (đúng lúc `SaveChangesAsync`, cùng transaction) → delivery service `UPDATE ... SET delivered=...` (lock) → `SELECT` đọc lại để gửi → **`DELETE FROM catalog.outbox_message` + `DELETE FROM catalog.outbox_state`** (dọn sau khi gửi thành công). Vì RabbitMQ đang sống, toàn bộ chu trình này diễn ra trong **dưới 1 giây** — `psql` sau đó thấy `outbox_message` **rỗng** (0 rows), không phải vì message không được ghi mà vì đã gửi xong và dọn sạch. → **Sửa lại kỳ vọng cho Commit 7:** muốn `psql` thấy row nằm yên trong `outbox_message`, bắt buộc phải tắt RabbitMQ TRƯỚC khi publish (đúng kịch bản Commit 7), không phải publish rồi mới đi `psql` xem khi broker vẫn sống.
  5. **Bằng chứng thật message tới RabbitMQ:** `curl` RabbitMQ Management API (`GET /api/exchanges`) → thấy exchange tên đúng theo full type name MassTransit convention: `TicketFlow.Contracts.Messaging.Catalog:CatalogEventPublished` (type `fanout`, `durable: true`), `message_stats.publish_in: 1` — xác nhận đúng 1 message thật đã publish vào exchange. `outgoing: []` vì chưa có consumer/queue nào bind (đúng dự kiến — Commit 5/6 mới thêm).
  6. Regression: full smoke test gateway 4/4 route `200`; `TicketFlow.Catalog.Domain.Tests` 35/35 pass.

---

## Nhóm C — Consumer services (mới)

### Commit 5 — `feat(notification): skeleton + consume CatalogEventPublished (log "sẽ gửi")`
- Service mới `src/Services/Notification/TicketFlow.Notification.Api` — scaffold từ `dotnet new web` (không phải `webapi`/Controllers, không phải Worker trần — đúng "host tối thiểu ASP.NET" đã quyết định ở mục 7 kiến trúc). Port **8005**. Không thêm `Microsoft.AspNetCore.OpenApi`/Scalar — không có endpoint HTTP nào để document (khác Catalog/Ordering), tránh package thừa.
- Ref `TicketFlow.Web` (health), `TicketFlow.Messaging`, `TicketFlow.Contracts.Messaging`. Program.cs chỉ 3 dòng lõi: `AddTicketFlowWeb()`, `AddTicketFlowMessaging(config, cfg => cfg.AddConsumer<CatalogEventPublishedConsumer>())`, `UseTicketFlowWeb()`.
- `Consumers/CatalogEventPublishedConsumer.cs` : `IConsumer<CatalogEventPublished>` → log `"[notification] sẽ gửi thông báo cho event {Title} ({CatalogEventId})"`. Idempotent tự nhiên (log không có side effect để lặp lại) — ghi chú rõ trong code, dời inbox/dedup thật sang lúc consumer bắt đầu ghi state (Phase 6, đã note ở mục "Hướng mở rộng").
- Thêm `docker-compose.yml` (`depends_on: rabbitmq healthy`, **không** thêm vào `depends_on` của gateway — notification không có route public, đúng quyết định kiến trúc mục 7), Dockerfile (theo đúng mẫu Ordering — service 1-project, không cần layer Domain/Application/Infrastructure), `.slnx`.
- **Sự cố hạ tầng gặp lại (2 lần liên tiếp):** mạng máy chủ rớt hẳn ngay lúc `docker compose build` (`dial tcp: lookup mcr.microsoft.com: no such host`) — khác lần Docker Desktop tự rớt ở Commit 3, lần này daemon vẫn sống nhưng **không có internet**. Lần đầu retry, `nuget.org` đã resolve được nhưng `mcr.microsoft.com` (registry base image .NET) vẫn timeout riêng — phải chờ cụ thể domain đó hồi phục (không phải outage toàn cục, có domain vẫn sống trong khi domain khác vẫn chết). Không phải lỗi code/cấu hình, chỉ là hạ tầng mạng chập chờn trong phiên làm việc này.
- **Verify thật (không mock), toàn bộ qua gateway + container logs thật:**
  1. `dotnet build` toàn solution: 0 Warning, 0 Error.
  2. Rebuild + `docker compose up -d notification` → log xác nhận đúng thứ tự: `Configured endpoint catalog-event-published, Consumer: TicketFlow.Notification.Api.Consumers.CatalogEventPublishedConsumer` (endpoint name tự sinh kebab-case — **lúc này hiểu nhầm là sinh từ tên message**, thực ra sinh từ **tên class consumer** `CatalogEventPublishedConsumer` bỏ hậu tố `Consumer`; sai lệch này chính là nguyên nhân bug phát hiện ở Commit 6) → `Bus started: rabbitmq://rabbitmq/`.
  3. `notification /health` → `200 Healthy`.
  4. **End-to-end thật — nửa đầu DoD Phase 3:** tạo + publish 1 event mới hoàn toàn qua gateway (full luồng event → seatmap → section → price tier → publish, cùng cách đã làm ở Commit 4) → `docker logs ticketflow-notification` xuất hiện đúng dòng `[notification] sẽ gửi thông báo cho event Phase 3 Commit 5 Verify Show (65e6947e-...)` — đúng Title và CatalogEventId của event vừa publish, không phải log giả định.
  5. Regression: `docker compose ps` đủ 10 container; full smoke test gateway 4/4 route `200`; `TicketFlow.Catalog.Domain.Tests` 35/35 pass.

### Commit 6 — `feat(search): skeleton + consume CatalogEventPublished (log "sẽ index")`
- Service mới `src/Services/Search/TicketFlow.Search.Api` — scaffold + wiring giống hệt Notification (Commit 5): `dotnet new web`, port **8006**, ref `TicketFlow.Web`/`TicketFlow.Messaging`/`TicketFlow.Contracts.Messaging`, không OpenAPI/Scalar.
- `Consumers/CatalogEventPublishedConsumer.cs` → log `"[search] sẽ index event {Title} ({CatalogEventId})"`. ES thật là **Phase 6 của roadmap** (không phải commit này) — Phase 3 chỉ chứng minh **fan-out nhiều consumer** (1 event → cả notification lẫn search cùng nhận).
- Thêm `docker-compose.yml` (`depends_on: rabbitmq healthy`, không vào gateway), Dockerfile (mẫu Ordering), `.slnx`.
- **🐛 Bug thật gặp và sửa — va chạm tên queue giữa 2 service (nghiêm trọng, phá fan-out):** log container search khi start lần đầu in ra **y hệt** dòng của notification: `Configured endpoint catalog-event-published, Consumer: TicketFlow.Search.Api.Consumers.CatalogEventPublishedConsumer`. Cả 2 consumer (ở 2 service khác nhau) đều đặt tên class `CatalogEventPublishedConsumer` — `KebabCaseEndpointNameFormatter` mặc định của MassTransit sinh queue name **từ tên class consumer** (bỏ hậu tố `Consumer`), không phải từ tên message/namespace. Verify qua RabbitMQ Management API xác nhận đúng lỗ hổng: `GET /api/queues` chỉ trả **1 queue** `catalog-event-published` với `"consumers": 2` — 2 service đang là **competing consumers trên cùng 1 queue**, nghĩa là mỗi message chỉ 1 trong 2 service nhận được (round-robin), không phải cả 2 cùng nhận. Fan-out coi như hỏng ngầm, sẽ không phát hiện được nếu chỉ đọc code mà không verify qua RabbitMQ thật.
  - **Sửa tận gốc ở `TicketFlow.Messaging`** (BuildingBlocks, không phải patch riêng từng service): `AddTicketFlowMessaging` thêm tham số bắt buộc `string serviceName`, dùng `SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter(serviceName, includeNamespace: false))` thay vì `SetKebabCaseEndpointNameFormatter()` mặc định — mọi queue giờ có tiền tố tên service (`notification-catalog-event-published`, `search-catalog-event-published`, `catalog-...` khi catalog tự consume gì đó sau này). Cập nhật cả 3 call site: `Catalog.Infrastructure` (`"catalog"`), `Notification.Api` (`"notification"`), `Search.Api` (`"search"`).
  - **Dọn dữ liệu:** xoá tay queue cũ sai `catalog-event-published` qua RabbitMQ Management API (`DELETE /api/queues/%2F/catalog-event-published`) sau khi rebuild+redeploy cả 3 service — không tự dọn, phải chủ động xoá vì queue durable không tự biến mất.
  - **Bài học tổng quát:** convention "tên class consumer trùng nhau giữa các service" (`XxxConsumer` là tên tự nhiên, dễ lặp lại) là bẫy thật với endpoint-name-formatter mặc định của MassTransit trong kiến trúc nhiều service cùng consume 1 message type — luôn cần prefix theo service, không chỉ theo tên message/consumer.
- **Verify thật (không mock), sau khi sửa bug:**
  1. `dotnet build` toàn solution: 0 Warning, 0 Error.
  2. Rebuild + redeploy cả 3 image (`catalog notification search`) → `GET /api/queues` xác nhận đúng **2 queue riêng biệt**: `notification-catalog-event-published`, `search-catalog-event-published`.
  3. **Fan-out thật — trọn vẹn DoD Phase 3 phần "nhiều consumer":** tạo + publish 1 event mới hoàn toàn qua gateway → `docker logs ticketflow-notification` VÀ `docker logs ticketflow-search` **đồng thời** xuất hiện log với **cùng 1** `CatalogEventId`, độc lập nhau (không phải round-robin — cả 2 cùng nhận full bản sao message).
  4. Regression: `docker compose ps` đủ **11 container**; full smoke test gateway 4/4 route `200`; `notification`/`search` `/health` đều `200`; `TicketFlow.Catalog.Domain.Tests` 35/35 pass; `TicketFlow.Reservation.IntegrationTests` 5/5 pass.

---

## Nhóm D — Chứng minh resiliency + chốt

### Commit 7 — `test(messaging): Outbox durability — tắt broker giữa chừng, event không mất`
- **Không có code mới** — đúng bản chất commit này: thuần verify runtime một property đã có sẵn nhờ thiết kế Outbox từ Commit 3, không phải viết thêm logic. Cân nhắc dựng 1 project test tự động (kiểu Testcontainers như Phase 2 Commit 10) nhưng quyết định **không làm** — khác Phase 2 (nơi Testcontainers chứng minh **logic Lua script tự viết**, một correctness claim thật của code mình), ở đây đang chứng minh hành vi **MassTransit's EF Core Outbox đã tự có tài liệu/test riêng** — verify runtime thủ công qua Docker thật là đủ, dựng test project riêng cho việc này là vàng hoá.
- **Kịch bản thật đã chạy (áp dụng đúng bài học từ Commit 4 — phải tắt broker TRƯỚC khi publish, không phải publish rồi mới tắt):**
  1. `docker stop ticketflow-rabbitmq` → xác nhận `Exited (137)`.
  2. Gọi full luồng API thật qua gateway (event → seatmap → section → price tier → publish) **trong lúc RabbitMQ đang tắt hẳn** → `publish` vẫn trả **`200`, `status: "Published"`** — chứng minh trực tiếp claim cốt lõi của Outbox: ghi Postgres **không phụ thuộc** broker sống hay chết, không có dual-write risk.
  3. `psql` xác nhận row nằm yên: `outbox_message` có đúng 1 row (`message_type: CatalogEventPublished`), `outbox_state.delivered` là **NULL** (chưa gửi). `body` JSON đầy đủ, đúng field (`catalogEventId`, `title`, `venueId`, `startsAt`, `publishedAt`) — message không mất, không hỏng.
  4. `docker start ticketflow-rabbitmq`, chờ Docker healthcheck báo `healthy` (**~13s**).
  5. Poll `outbox_message` mỗi 1s → **row biến mất (drain xong) chỉ sau ~1s kể từ lúc RabbitMQ healthy** — nhanh hơn dự kiến, vì `BusOutboxDeliveryService` poll liên tục ở tần suất cao khi có row pending, không đợi hết 1 chu kỳ retry dài.
  6. `docker logs ticketflow-notification` **và** `docker logs ticketflow-search` **đồng thời** xuất hiện log cho đúng event vừa publish lúc broker tắt (`Phase 3 Commit 7 Durability Verify`) — không chỉ outbox trống, mà **cả 2 consumer thật sự nhận được** message đã bị trễ.
- **Bằng chứng thật hành vi retry** (đọc log catalog quanh mốc broker tắt): `Connection Failed: rabbitmq://rabbitmq/` → `RabbitMQ.Client.Exceptions.BrokerUnreachableException: None of the specified endpoints were reachable`, exception này được **bắt bên trong `MassTransit.EntityFrameworkCoreIntegration.BusOutboxDeliveryService<TDbContext>.DeliverOutboxMessages`** — nghĩa là delivery service tự catch lỗi kết nối, log lại, và tiếp tục vòng poll tiếp theo (`SELECT ... FOR UPDATE SKIP LOCKED` lặp lại đều đặn) **mà không crash, không cần retry code tự viết**. Đây chính là lý do Commit 1 pin `MassTransit.RabbitMQ`/`MassTransit.EntityFrameworkCore` — cơ chế resiliency này đến từ thư viện, không phải phải tự dựng.
- **Verify thật (không mock):**
  1. Toàn bộ kịch bản trên chạy qua Docker + gateway thật, không mock broker, không mock DB.
  2. Regression sau khi RabbitMQ sống lại: `docker compose ps` đủ 11 container; full smoke test gateway 4/4 route `200`; `notification`/`search` `/health` đều `200`; `TicketFlow.Catalog.Domain.Tests` 35/35 pass.
- **Kết luận DoD:** đã chứng minh đầy đủ "tạo sự kiện ở catalog → event chảy qua bus → notification nhận được; tắt broker giữa chừng, bật lại → event không mất (Outbox)" — cả 2 vế của DoD Phase 3, bằng dữ liệu thật, không suy đoán.

### Commit 8 — `chore: finalize Phase 3 — slnx, docker-compose, DoD checklist`
- **Verify thật:**
  1. `dotnet sln TicketFlow.slnx list` → đủ **23 project**, gồm 4 project mới của Phase 3: `TicketFlow.Contracts.Messaging`, `TicketFlow.Messaging`, `TicketFlow.Notification.Api`, `TicketFlow.Search.Api`.
  2. `dotnet build` toàn solution: 0 Warning, 0 Error.
  3. `docker-compose.yml`: xác nhận `notification`/`search` có `depends_on: rabbitmq (healthy)`, **không** nằm trong `depends_on` của `gateway` (đúng quyết định kiến trúc — chưa có route public).
  4. `docker compose ps` → đủ **11 container** (catalog, gateway, identity, kafka, notification, ordering, postgres, rabbitmq, redis, reservation, search).
  5. Full smoke test: catalog/identity/ordering/reservation qua gateway đều `200`; `notification`/`search`/`catalog` `/health` đều `200`.
  6. `TicketFlow.Catalog.Domain.Tests` 35/35 pass.
- **Quyết định chốt (đã hỏi & được duyệt):** sửa `scope.md` Phase 3 — bỏ "Kafka Rider" khỏi mục 1, ghi rõ lý do dời sang Phase 6 (đúng phản biện mục 5 phần "Quyết định kiến trúc" ở trên); sửa luôn Phase 6 thêm ghi chú "bật Kafka Rider ở đây". Cũng nhân tiện sửa mục 3 scope.md Phase 3: đổi `EventPublished` → `CatalogEventPublished` cho khớp tên contract thật đã dùng (tránh gây hiểu lầm về sau khi đọc lại scope.md, vì domain event `EventPublished` đã tồn tại từ Phase 1 với ý nghĩa khác hẳn).
- Tick DoD Phase 3 trong `scope.md`, mirror đúng pattern đã dùng cho Phase 1/2 (`✅ ... **Hoàn thành [ngày]** — ... xem docs/phase-3-plan.md`).

---

## Kiểm chứng DoD cuối Phase 3

| DoD | Kiểm chứng |
|---|---|
| Event chảy catalog → bus → notification nhận | Commit 5: publish event ở catalog → `docker logs ticketflow-notification` thấy log "sẽ gửi" |
| Fan-out nhiều consumer | Commit 6: 1 event → cả notification + search cùng nhận (pub/sub) |
| Broker chết giữa chừng → event KHÔNG mất | Commit 7: stop rabbitmq → publish (row nằm trong `outbox_message`) → start rabbitmq → notification eventually nhận |
| Publish atomic với thay đổi DB | Outbox: message + business change trong cùng transaction Postgres (không dual-write) |

## Hướng mở rộng (backlog — không thuộc Phase 3)
- **Kafka Rider**: bật ở Phase 6 khi `analytics-service` cần stream doanh số → ClickHouse. `AddTicketFlowMessaging` thiết kế sẵn để thêm rider không phá vỡ.
- **Inbox/idempotency thật** ở consumer: bật khi notification/search bắt đầu ghi state (Phase 6).
- **Integration event versioning**: khi contract đổi shape, dùng versioned message type / migration — ghi lại khi thực sự gặp.

## Thứ tự đọc sách trước khi code (scope mục 13)
Đọc **Ch4 (Communication patterns)** — trọng tâm **asynchronous messaging**, **publish/subscribe**, và **Transactional Outbox pattern**. Bổ sung ngoài sách: MassTransit docs về **EF Core Outbox** (`AddEntityFrameworkOutbox`), **consumer idempotency/inbox**, và **retry/redelivery/DLQ**; tài liệu RabbitMQ về **durable queue + persistent message** để hiểu độ bền phía broker (bổ trợ Outbox phía publisher). Đọc lướt so sánh **RabbitMQ vs Kafka** để nắm vì sao Phase 3 chọn RabbitMQ, Kafka để Phase 6.
