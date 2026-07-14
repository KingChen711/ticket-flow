---
title: "Phase 4 — CQRS + Event Sourcing (Ordering) — Kế hoạch commit"
phase: 4
chapters: [Ch6 CQRS, Ch7 Event Sourcing, Marten]
created: 2026-07-14
tags: [ticketflow, phase-4, plan, marten, event-sourcing, cqrs, ordering]
---

# Phase 4 — Kế hoạch chi tiết theo commit

> **Mục tiêu (từ scope mục 10):** tách read/write (CQRS), lưu lịch sử dạng event (Event Sourcing). Biến `ordering` từ skeleton gRPC-client thành service event-sourced đầy đủ.
> **DoD:** đặt 1 order sinh ra **stream event**; **xoá read model rồi replay từ event dựng lại được**.

> ⚠️ **Viết TRƯỚC khi code** (giống [phase-2](phase-2-plan.md)/[phase-3-plan.md](phase-3-plan.md)). Các mục "lệch kế hoạch / bug tự bắt được" và "verify thật" bổ sung dần khi thực thi. Version package chốt vào `Directory.Packages.props` lúc code.

---

## Bối cảnh: cái gì đã có, cái gì Phase 4 thêm

- **Ordering hiện tại là skeleton 1-project** (`TicketFlow.Ordering.Api`): chỉ có gRPC client gọi reservation + endpoint REST `/check-seats` (bridge Ch3, Phase 1). **Chưa có DB, chưa có tầng Domain/Application/Infrastructure.**
- **Đây là service ĐẦU TIÊN không dùng EF Core.** Catalog/Identity/Reservation đều state-based (lưu *trạng thái hiện tại* qua EF Core + Postgres/Redis). Ordering sẽ **event-sourced**: lưu *chuỗi sự kiện*, state = fold(events). Đây chính là điểm học Ch7.
- **Phase 4 thêm:** DB `ticketflow_ordering`, Marten (event store + projection), tầng Domain (Order aggregate event-sourced) / Application (command + query) / Infrastructure (Marten) / Api. Giữ nguyên endpoint `/check-seats` cũ (không đụng).
- **KHÔNG thuộc Phase 4:** orchestration giữ ghế → thanh toán → xác nhận (đó là **PurchaseSaga, Phase 5**). Phase 4 giữ Order **tự chứa**: PlaceOrder chỉ ghi lại *ý định đặt*, chưa gọi reservation để hold, chưa publish integration event lên bus. Tránh kéo saga vào sớm.

---

## Quyết định kiến trúc cho Phase 4

### 1. Event Sourcing — lưu event, không lưu state (điểm học Ch7, tương phản với 3 service trước)
- **State-based (Catalog...):** UPDATE 1 row → mất lịch sử, chỉ biết "hiện tại". **Event-sourced (Ordering):** append event vào stream, **không bao giờ UPDATE/DELETE event** → giữ toàn bộ lịch sử "chuyện gì đã xảy ra theo thứ tự".
- **Order aggregate = fold trái các event:** `Apply(OrderPlaced)`, `Apply(OrderConfirmed)`, `Apply(OrderCancelled)` — mỗi method biến đổi state hiện tại. Load 1 order = đọc stream + fold từ đầu (Marten `AggregateStreamAsync<Order>`). State không được lưu trực tiếp; nó là **kết quả tính toán** từ event.
- **Domain event ở Phase 4 KHÁC domain event Phase 1-3:** ở Catalog, domain event chỉ là thông báo phụ (`Raise(...)`, đang `Ignore(DomainEvents)` ở EF). Ở Ordering, **event CHÍNH LÀ dữ liệu được lưu** — chúng là source of truth, không phải phụ phẩm. Đây là khác biệt tư duy cốt lõi.

### 2. Marten làm event store — sạch license, tự quản schema (khác EF)
- **Marten 9.x, MIT license** (xác nhận qua nuspec, có target `net10.0`) — không vướng thương mại như MassTransit/MediatR. Marten là **event store + document DB + projection engine** trên Postgres, tất cả trong một.
- **Marten tự quản schema, KHÔNG dùng EF migrations:** khác Catalog/Reservation (migrate-on-startup qua `dotnet ef`). Marten dev-mode dùng `AutoCreate.CreateOrUpdate` tự dựng bảng `mt_events`/`mt_streams`/projection docs khi app chạy. Điểm học: **2 chiến lược schema management khác nhau trong cùng hệ thống** (polyglot persistence ở tầng công cụ, nối tiếp bài học Ch8 Phase 2).
- **DB riêng `ticketflow_ordering`** (giữ DB-per-service). Marten sống trong schema riêng của nó trong DB này.

### 3. MediatR — có chuyện license Y HỆT MassTransit, và có thể KHÔNG cần (phản biện scope)
- Scope mục 3 ghi *"MediatR: tách Command và Query"*. Nhưng: **MediatR v13.0.0+ đã chuyển giấy phép thương mại** (`license type="file"`, giống MassTransit v9). **v12.5.0 là bản Apache-2.0 miễn phí cuối cùng** (chỉ target `net6.0`/`netstandard2.0` — vẫn chạy trên net10.0 vì là lib nhỏ, nhưng đã đóng băng, không có target net10 riêng).
- **Phản biện:** CQRS **không đòi hỏi MediatR**. "Tách Command và Query" là tách **model đọc khỏi model ghi** (điểm học Ch6), KHÔNG phải "dùng thư viện mediator". Có thể inject handler thẳng vào endpoint, hoặc tự cuộn convention `ICommandHandler<TCmd,TRes>` / `IQueryHandler<TQry,TRes>` (~15 dòng, 0 package). Giá trị thật của MediatR là **pipeline behaviors** (validation/logging/transaction) — mà Phase 4 DoD **không cần**; khi cần cross-cutting thật thì là chuyện Phase 5 (saga/resiliency).
- **Đề xuất (khuyến nghị):** **tự cuộn dispatcher tối thiểu**, không kéo MediatR. Lý do: (a) v13+ thương mại, v12 đóng băng — tránh phụ thuộc chết; (b) đúng kỷ luật "không kéo package lớn cho nhu cầu hẹp" đã áp cho ASP.NET Identity (Phase 1) và Swashbuckle; (c) tự cuộn dạy rõ cơ chế dispatch hơn, đúng tinh thần học. Marten đã lo phần khó (ES + projection); mediator chỉ là lớp mỏng.
- **Phương án thay thế** (nếu muốn "chuẩn sách/industry"): pin **MediatR 12.5.0** (Apache-2.0 cuối), song song tiền lệ MassTransit v8. → **Chờ bạn duyệt ở Commit 3** (nơi command side thật sự cần dispatcher); mặc định plan đi theo hướng tự cuộn.

### 4. CQRS = tách MODEL, không phải tách công cụ (điểm học Ch6)
- **Write model:** Order aggregate, dựng lại bằng fold stream event (`AggregateStreamAsync`) mỗi lần xử lý command. Đúng đắn (consistency), nhưng "chậm" — chấp nhận được vì write ít, cần chính xác.
- **Read model:** **projection** — Marten tự duy trì 1 document `MyOrderView` (đơn của tôi) cập nhật theo event. Query đọc thẳng document này (nhanh, không fold). Không bao giờ query bằng cách fold stream ở đường đọc.
- **Đây là bản chất CQRS:** 2 model cho 2 mục đích, cùng nguồn sự thật (event stream). Không phải "2 database" hay "2 microservice".

### 5. Projection: inline vs async — chọn inline cho Phase 4
- **Inline projection:** cập nhật read model **trong cùng transaction** với append event → read-after-write nhất quán ngay. Đơn giản, đủ cho Phase 4.
- **Async projection (daemon):** cập nhật read model ở background → eventual consistency, chịu tải cao hơn. Mạnh hơn nhưng phức tạp (cần chạy projection daemon). **Hoãn** — ghi chú là hướng mở rộng, Phase 4 dùng inline để giữ DoD rõ ràng và dễ verify.
- **Replay (DoD):** dù inline hay async, Marten cho **rebuild projection từ đầu** — xoá document read model, chạy lại projection over toàn bộ event stream → dựng lại y hệt. Đây chính là thứ DoD ép chứng minh, và là **siêu năng lực của Event Sourcing**: read model chỉ là cache có thể vứt đi và tái tạo.

### 6. Order lifecycle — đủ event để fold có ý nghĩa
- Events: **`OrderPlaced`** (customerId, eventId, seatIds, tổng tiền) → **`OrderConfirmed`** → hoặc **`OrderCancelled`**. Đủ 3 loại để `Apply` fold ra state khác nhau (`Pending`→`Confirmed`/`Cancelled`), đủ minh hoạ chuỗi. Không nhồi thêm event (payment/refund là Phase 5).
- **Optimistic concurrency:** Marten kiểm version stream khi append — 2 command đồng thời lên cùng order → 1 fail. Ghi chú, verify nhẹ (không phải trọng tâm như overselling Phase 2).

### 7. Quan hệ với reservation/saga — Phase 4 tự chứa
- PlaceOrder **KHÔNG** gọi reservation hold, **KHÔNG** publish lên bus. Chỉ ghi event vào stream Ordering. Việc nối "order → hold ghế → thanh toán → confirm/nhả" là **PurchaseSaga Phase 5**. Giữ endpoint `/check-seats` (gRPC client cũ) nguyên vẹn — nó độc lập, không dính CQRS.
- Không thêm gRPC server cho Ordering (chỉ giữ client sẵn có). Port REST **8004** giữ nguyên.

### 8. Kế thừa & hạ tầng
- Central Package Management, native OpenAPI + Scalar, exception middleware, health check (thêm Marten/Postgres health), `docker-compose.yml` (ordering thêm `depends_on: postgres healthy`), Dockerfile cập nhật COPY project mới, `.slnx`.
- **Health check:** thêm check Postgres/Marten cho ordering (trước giờ ordering không có DB nên không có DB health).

---

## Nhóm A — Domain event-sourced + Marten event store

### Commit 1 — `feat(ordering): event-sourced Order aggregate + domain events`
- Project mới `TicketFlow.Ordering.Domain` (ref `TicketFlow.Domain`). Mổ ordering từ skeleton thành có tầng Domain.
- `OrderStatus` enum (Pending/Confirmed/Cancelled).
- **Quyết định thêm khi code (không có trong bản nháp gốc):** field ban đầu định tên `EventId` (id của catalog event mà order đặt vé) — **đổi ngay thành `CatalogEventId`** để nhất quán với convention đã lập từ Phase 3 (tránh nhầm với khái niệm "event" nói chung trong ngữ cảnh Event Sourcing, nơi từ "event" đã bị dùng cho nghĩa khác — càng dễ nhầm hơn Catalog Phase 1-3).
- **Quyết định kiến trúc chốt khi code (đáng chú ý — lệch khỏi ấn tượng ban đầu của plan):** 3 event record (`OrderPlaced`, `OrderConfirmed`, `OrderCancelled`) là **record trần, KHÔNG kế thừa `TicketFlow.Domain.DomainEvent`**. Lý do: `DomainEvent` (EventId + OccurredOn) phục vụ pattern "buffer nội bộ rồi dispatch qua outbox" của Catalog/Reservation (state-based); ở đây event **CHÍNH LÀ dữ liệu Marten lưu**, đã có sẵn envelope riêng (id, timestamp, sequence, version theo stream) — kế thừa `DomainEvent` sẽ tạo 2 bộ metadata trùng lặp, gây nhầm "cái nào mới là timestamp thật". Đây là điểm khác biệt tư duy cụ thể hoá cho mục 1 phần "Quyết định kiến trúc".
- `Order : Entity<Guid>` (KHÔNG kế thừa `AggregateRoot<Guid>` — lý do tương tự: máy `Raise()`/`DomainEvents`/`ClearDomainEvents` của `AggregateRoot` là cách theo dõi "sự kiện mới" thứ 2, trùng lặp và gây nhầm lẫn với event stream Marten đã quản). Thiết kế:
  - `Apply(OrderPlaced/OrderConfirmed/OrderCancelled)` — **mutator DUY NHẤT**, dùng để fold.
  - `Order.Place(...)` — static, validate rồi **trả về event** (chưa có aggregate để gọi instance method).
  - `Confirm(now)`/`Cancel(reason, now)` — instance method, **quyết định thuần** (pure decision): validate trạng thái hiện tại rồi trả về event, **KHÔNG tự mutate** — tách bạch rạch ròi "quyết định" khỏi "áp dụng", đúng pattern Decide/Evolve của ES.
  - `Order.Load(IEnumerable<object> events)` — **thêm ngoài kế hoạch gốc**: factory fold toàn bộ lịch sử. Lý do thêm: cần một cách sạch để unit test fold mà không đụng reflection thủ công trong test, đồng thời Commit 3 có thể tái dùng nếu cần load aggregate ngoài đường Marten trực tiếp.
  - ⚠️ **Đính chính (phát hiện ở Commit 2):** câu mô tả gốc ở đây từng viết `AggregateStreamAsync` fold "qua reflection lên các overload Apply" — **sai**. Marten bản 9.x KHÔNG dùng reflection lúc runtime để dispatch `Apply`; nó dùng **source generator biên dịch thời gian build** (`JasperFx.Events.SourceGenerator`). Không có runtime fallback. Xem chi tiết ở phần verify Commit 2 bên dưới — đây là phát hiện quan trọng nhất của cả 2 commit đầu Phase 4.
- `Money` (VO) tạo riêng cho Ordering (`Orders/Money.cs`), **KHÔNG** tái dùng `TicketFlow.Catalog.Domain.Events.Money` dù cấu trúc giống hệt — Ordering không được reference `Catalog.Domain` (đúng nguyên tắc DB-per-service/no-cross-context-reference đã giữ xuyên suốt Phase 1-3). Trùng lặp ~15 dòng chấp nhận được, đổi lấy tính tự chủ bounded-context.
- **Verify thật:** `TicketFlow.Ordering.Domain.Tests` (xUnit, mẫu y hệt `Catalog.Domain.Tests`) — **8 test**, thuần domain, không DB: `Place` validate (seatIds rỗng → throw), `Place`→`Load` dựng đúng state `Pending` với dữ liệu khớp, fold `Placed`+`Confirmed`→`Confirmed`, fold `Placed`+`Cancelled`→`Cancelled`, `Confirm()` trả event mà **không mutate** aggregate gốc (assert `order.Status` vẫn `Pending` sau khi gọi `Confirm()`), guard chặn confirm-khi-đã-confirm / cancel-khi-đã-cancel / cancel-khi-đã-confirm. Chạy `dotnet test` → **8/8 pass**, 54ms.
- Build toàn solution: 0 Warning, 0 Error. Regression: `Catalog.Domain.Tests` 35/35 pass (không ảnh hưởng — commit này chỉ thêm project mới, cô lập).

### Commit 2 — `feat(ordering): Marten event store (Postgres)`
- **Lệch trình tự so với plan gốc:** plan viết "Project mới Infrastructure (ref Application)" như thể Application đã tồn tại — thực ra chưa (Nhóm B/Commit 3 mới định tạo Application). Xử lý: tạo `TicketFlow.Ordering.Application` **tối thiểu** ngay trong commit này (chỉ có `Abstractions/IOrderStore.cs` + `ServiceCollectionExtensions.AddOrderingApplication` đăng ký `TimeProvider.System`), rồi Infrastructure ref vào đó — đúng thứ tự Domain→Application→Infrastructure đã giữ xuyên suốt Phase 1-3, chỉ là gộp "ports" và "adapter" vào cùng 1 commit vì port phải tồn tại trước khi adapter implement được. Commit 3 sẽ **bổ sung** command handlers/DTOs vào Application đã có sẵn, không tạo lại.
- Pin `Marten` **9.15.2** vào `Directory.Packages.props` (MIT, xác nhận net10.0 target qua nuspec).
- `IOrderStore` (Application): `LoadAsync`/`StartStream`/`Append`/`SaveChangesAsync` — mô phỏng đúng shape `IDocumentSession` của Marten (buffer rồi commit), cùng tinh thần `IUnitOfWork` đã dùng cho EF Core ở service khác.
- `MartenOrderStore : IOrderStore` (Infrastructure): `LoadAsync` → `session.Events.AggregateStreamAsync<Order>(...)` (live aggregation — fold toàn bộ event mỗi lần load, không cần đăng ký projection riêng cho việc này).
- `AddOrderingInfrastructure`: `AddMarten(options => { options.Connection(...); options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate; })` (dev-only, cùng tinh thần migrate-on-startup của EF); đăng ký `IOrderStore`; health check.
- **🐛 Bug thật #1 — namespace `AutoCreate` đoán sai:** thử `using Weasel.Core;` (theo hiểu biết cũ về Marten) → `CS0103`. Viết throwaway console app reference thẳng gói `Marten` qua NuGet (không qua project — để mọi transitive dependency tự resolve đúng), dùng reflection load `typeof(StoreOptions).GetProperty("AutoCreateSchemaObjects").PropertyType.FullName` → tìm ra đúng: **`JasperFx.AutoCreate`**. Hệ sinh thái Marten bản mới đã tổ chức lại nhiều type nền tảng dưới namespace `JasperFx` (JasperFx.Core, JasperFx.Events...), khác tài liệu/ấn tượng cũ.
- **🐛 Bug thật #2 (nghiêm trọng hơn, phát hiện qua verify runtime, không phải build) — health check âm thầm tạo sai loại bảng:** `MartenHealthCheck` bản đầu dùng `session.Query<Order>().Take(1)...` — chạy được, `/health` trả `200 Healthy`, **nhưng** `psql` cho thấy Marten tạo bảng `mt_doc_order` thay vì `mt_events`/`mt_streams`. Nguyên nhân: `session.Query<T>()` là API **document store** (LINQ trên document đã lưu), hoàn toàn khác API **event store** (`session.Events...`) — gọi nhầm khiến Marten hiểu `Order` là 1 document type thường cần bảng riêng, âm thầm tạo schema sai không hề báo lỗi. **Sửa:** đổi health check dùng `session.Events.QueryAllRawEvents().Take(1)...` — đúng API event store, không tạo bảng document. Dọn bảng `mt_doc_order` sai qua `DROP TABLE`. **Bài học:** `Order` (event-sourced) không bao giờ được gọi qua `session.Query<Order>()` — chỉ được dựng lại qua fold stream; đây là ranh giới API dễ nhầm nhất khi mới dùng Marten.
- **🐛 Bug thật #3 (nghiêm trọng nhất, xung đột thật với triết lý Clean Architecture) — source generator phải chạy trên assembly Domain:** verify thật bằng scratch console app gọi `StartStream`→`SaveChangesAsync`→`LoadAsync` → ném `JasperFx.Events.Projections.InvalidProjectionException: No source-generated dispatcher found... there is no runtime fallback`. Marten 9.x **không** dispatch `Apply(...)` bằng reflection lúc chạy (khác hiểu biết/kế hoạch ban đầu) — nó dùng **source generator biên dịch thời gian build** (`JasperFx.Events.SourceGenerator`), và generator này **phải chạy trên chính assembly định nghĩa aggregate** (`TicketFlow.Ordering.Domain`, nơi `Order` sống), không phải assembly nào gọi Marten runtime (`Infrastructure`).
  - **Xung đột kiến trúc thật:** Domain xuyên suốt Phase 1-4 luôn "sạch", không phụ thuộc framework nào. Marten đòi hỏi ngược lại.
  - **Giải quyết (đúng pattern đã có sẵn trong repo):** thêm `PackageReference Marten` vào **`TicketFlow.Ordering.Domain.csproj`** nhưng **chỉ lấy analyzer** (`IncludeAssets="analyzers"`, `PrivateAssets="all"`) — y hệt cách `Microsoft.EntityFrameworkCore.Design` được thêm analyzer-only vào các `*.Api` project (Phase 1). Domain **vẫn zero runtime dependency** lên Marten — `Order.cs` không có dòng `using Marten;` nào, không implement interface Marten nào; chỉ có **build-time tooling** chạm vào assembly để sinh code.
  - **Lớp thứ 2 của cùng vấn đề:** chỉ đưa `analyzers` là chưa đủ — code sinh ra (`...Evolver.g.cs`) tham chiếu thẳng type `JasperFx.Events.*`, cần **runtime assembly thật** để biên dịch được (không chỉ analyzer). Thêm `PackageReference Include="JasperFx.Events"` (pin 2.27.0, MIT, net10.0, **không kéo theo Postgres/Npgsql/Marten proper** — đây là lib lõi event-sourcing transport-agnostic mà Marten xây trên đó) làm dependency runtime thật cho Domain. Đây là ranh giới chấp nhận được: Domain phụ thuộc *khái niệm event sourcing* (JasperFx.Events), không phụ thuộc *Marten-cụ-thể-trên-Postgres*.
- **Verify thật cuối cùng (không mock), qua scratch console app gọi thẳng `MartenOrderStore` với Postgres local thật:**
  1. `Order.Place(...)` → `StartStream` → `SaveChangesAsync` → `LoadAsync` ngay sau → đúng `Status: Pending`, đúng `SeatIds`, đúng `Total` (khớp dữ liệu đưa vào).
  2. `order.Confirm(now)` → `Append` → `SaveChangesAsync` → `LoadAsync` lại → đúng `Status: Confirmed` (fold **2 event** đúng thứ tự).
  3. `LoadAsync` với `orderId` không tồn tại → trả `null` (không throw) — đúng hợp đồng `IOrderStore.LoadAsync`.
  4. → **PASS** cả 3 kịch bản.
  5. `psql` xác nhận đúng bảng event-store thật: `mt_events`, `mt_streams`, `mt_event_progression`, `mt_high_water_skips`, `mt_doc_deadletterevent` — **không còn** `mt_doc_order`. `SELECT` trực tiếp `mt_events` thấy đúng `type: order_placed`/`order_confirmed`, đúng `version` 1/2 cùng `stream_id`.
  6. Docker thật: tạo DB `ticketflow_ordering` thủ công trước (`CREATE DATABASE` — khác EF Core, Marten **không** tự tạo database, chỉ tự tạo *schema objects* trong DB đã tồn tại sẵn; đây là điểm khác biệt thật giữa 2 chiến lược schema management, đúng như quyết định kiến trúc mục 2 đã dự đoán, giờ có bằng chứng cụ thể). Cập nhật Dockerfile (thêm COPY layer cho 3 project mới) + `docker-compose.yml` (`ordering` thêm `depends_on: postgres healthy`). Rebuild image → `docker compose up -d ordering` → log sạch, không lỗi → `ordering /health` → `200 Healthy` trong container thật.
  7. Regression: full smoke test gateway (catalog/identity/reservation `200`, `ordering/check-seats` gRPC cũ vẫn `200` — không đụng), `notification`/`search`/`ordering` `/health` đều `200`, `docker compose ps` đủ **11 container**. `Catalog.Domain.Tests` 35/35, `Ordering.Domain.Tests` 8/8 — không regression.
- **Verify thật:** app start → Marten tự tạo bảng `mt_events`/`mt_streams` trong `ticketflow_ordering` (psql xác nhận); append thử 1 event → `mt_events` có row; `AggregateStreamAsync` fold lại đúng.

---

## Nhóm B — CQRS write side (commands)

### Commit 3 — `feat(ordering): command side — PlaceOrder/Confirm/Cancel`
- Project mới `TicketFlow.Ordering.Application` (ref Domain). **Quyết định dispatcher** (xem kiến trúc mục 3): mặc định tự cuộn `ICommandHandler<TCmd,TRes>` (chờ bạn duyệt nếu muốn MediatR 12.5.0 thay thế).
- Commands + handlers: `PlaceOrderCommand` → `StartStream` với `OrderPlaced`; `ConfirmOrderCommand`/`CancelOrderCommand` → load aggregate, guard, append event vào stream có sẵn (optimistic concurrency).
- DTOs: `PlaceOrderRequest/Response`, ...
- **Verify:** handler test + verify stream event tích luỹ đúng.

### Commit 4 — `feat(ordering): command endpoints (REST)`
- Api: `POST /orders` (place), `POST /orders/{id}/confirm`, `POST /orders/{id}/cancel`. Ref Application + Infrastructure, đăng ký DI, health.
- **Verify thật qua gateway:** place → confirm 1 order → `psql` `mt_events` thấy đúng **2 event** (`OrderPlaced` + `OrderConfirmed`) trong 1 stream, đúng thứ tự; load lại order → state `Confirmed`.

---

## Nhóm C — CQRS read side (projection + queries)

### Commit 5 — `feat(ordering): MyOrders projection (read model)`
- Infrastructure: projection **inline** `MyOrderView` (OrderId, CustomerId, EventId, SeatIds, Status, Total, thời điểm) tự cập nhật theo `OrderPlaced`/`Confirmed`/`Cancelled`. Đăng ký `projections.Add<MyOrdersProjection>(ProjectionLifecycle.Inline)`.
- **Verify thật:** place/confirm order → `psql` bảng document projection có row `MyOrderView` với Status khớp event mới nhất (read-after-write nhất quán, vì inline).

### Commit 6 — `feat(ordering): query side — GetMyOrders/GetOrderById`
- Application: `GetMyOrdersQuery(customerId)` + `GetOrderByIdQuery` + handlers **đọc thẳng projection document** (KHÔNG fold stream ở đường đọc). Query endpoints `GET /orders?customerId=...`, `GET /orders/{id}`.
- **Verify thật qua gateway:** đặt vài order cho 1 customer → `GET /orders?customerId=` trả đúng danh sách từ read model; đối chiếu với `mt_events` (write side) → khớp.

---

## Nhóm D — Chứng minh replay + chốt

### Commit 7 — `test(ordering): replay — xoá read model, rebuild từ event`
- Kịch bản DoD phần 2 (verify thật, không mock):
  1. Đặt N order (có confirm/cancel) → read model `MyOrderView` có N document, `mt_events` có chuỗi event đầy đủ.
  2. **Xoá sạch read model** (truncate document projection, giữ nguyên `mt_events`).
  3. **Rebuild projection** từ toàn bộ event stream (Marten projection rebuild / daemon rebuild API).
  4. Query lại → read model dựng lại **y hệt** trước khi xoá (so từng document).
- **Chứng minh:** read model chỉ là cache tái tạo được; event stream là source of truth duy nhất. Đây là siêu năng lực ES.

### Commit 8 — `chore: finalize Phase 4 — slnx, docker-compose, DoD checklist`
- Rà `.slnx` đủ project mới; `docker compose ps` đủ container; ordering thêm `depends_on: postgres`; smoke test toàn hệ.
- Tick DoD Phase 4 trong `scope.md`; nếu chọn tự cuộn dispatcher (không MediatR) thì ghi chú sửa scope mục 3 (bỏ "MediatR" hoặc nói rõ tự cuộn). Cập nhật doc này.

---

## Kiểm chứng DoD cuối Phase 4

| DoD | Kiểm chứng |
|---|---|
| Đặt 1 order sinh ra stream event | Commit 4: `POST /orders` → `psql mt_events` thấy `OrderPlaced` trong stream mới |
| Nhiều event fold ra state đúng | Commit 4: place→confirm → 2 event 1 stream → `AggregateStreamAsync` cho `Confirmed` |
| Read/write tách (CQRS) | Commit 5-6: write fold aggregate; read đọc projection `MyOrderView`, không fold |
| Xoá read model → replay dựng lại | Commit 7: truncate projection → rebuild từ `mt_events` → read model y hệt |

## Hướng mở rộng (backlog — không thuộc Phase 4)
- **Async projection (daemon):** chuyển `MyOrders` sang async khi cần chịu tải / nhiều projection (Phase 6 analytics).
- **Publish integration event khi OrderPlaced/Confirmed:** nối Ordering vào bus (Phase 3 messaging) để **PurchaseSaga Phase 5** điều phối hold→pay→confirm.
- **MediatR pipeline behaviors:** nếu về sau cần validation/logging/transaction cross-cutting và chọn MediatR — cân nhắc pin v12.5.0 (Apache-2.0 cuối) hoặc tự cuộn decorator.

## Thứ tự đọc sách trước khi code (scope mục 13)
Đọc **Ch6 (CQRS)** — tách command/query, read model vs write model, vì sao tách; và **Ch7 (Event Sourcing)** — event store, stream, aggregate qua fold, projection, replay/rebuild, optimistic concurrency, so sánh state-based vs event-sourced. Bổ sung ngoài sách: **Marten docs** — event store (`StartStream`/`AggregateStreamAsync`), **projections** (inline vs async/daemon, self-aggregate), projection **rebuild**, schema management (`AutoCreate`). Đọc lướt **so sánh với state-based** (3 service EF Core trước) để thấy rõ đánh đổi.
