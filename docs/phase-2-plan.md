---
title: "Phase 2 — Reservation & bài toán overselling — Kế hoạch commit"
phase: 2
chapters: [Ch8 Database strategies, Redis distributed locking]
created: 2026-07-13
tags: [ticketflow, phase-2, plan, redis, concurrency, overselling]
---

# Phase 2 — Kế hoạch chi tiết theo commit

> **Mục tiêu (từ scope mục 10):** giải quyết concurrency — điểm nhấn distributed systems. Giữ ghế bằng Redis (hold có TTL), chống overselling.
> **DoD:** chạy k6/NBomber song song 1000 request giành **cùng 1 ghế** → đúng 1 request thắng ghế, phần còn lại nhận "hết chỗ".

> ⚠️ **Khác với [phase-1-plan.md](phase-1-plan.md):** file đó được viết *trong lúc* code nên đã ghi sẵn các bug/lệch kế hoạch thực tế. File này viết **trước khi code** — các mục "lệch kế hoạch / bug tự bắt được" sẽ được bổ sung dần khi thực thi từng commit (giống cách Phase 1 tiến hoá).

---

## Câu hỏi Phase 1 để ngỏ, giờ chốt

Phase 1 (Commit 3.1) cố tình **chưa** model `Seat` cá thể — `Section` chỉ giữ `Capacity` dạng số đếm, và ghi rõ: *"việc giữ đúng 1 ghế cụ thể là bài toán của reservation-service ở Phase 2, lúc đó mới quyết định có explode ra Seat entity hay không."*

**Quyết định:** **CÓ, explode thành `Seat` cá thể.** Bắt buộc, vì DoD yêu cầu "1000 request giành **cùng 1 ghế**, đúng 1 thắng" — muốn có "cùng 1 ghế" để 1000 người tranh thì ghế phải **định danh được**. Không thể chứng minh overselling bằng số đếm thuần.

Nhưng **không** dựng sơ đồ ghế dạng lưới hàng×cột trực quan (Row/Number/toạ độ) — đó là bài toán của `organizer-dashboard`/`gate-scanner` ở **Phase 8** (frontend). Phase 2 chỉ cần ghế có **id + nhãn** (`{sectionName}-{n}`, ví dụ `VIP-1`..`VIP-500`), sinh ra từ `Capacity` của section. Tránh vàng hoá (mục 12 scope.md).

---

## Quyết định kiến trúc cho Phase 2

- **Seat-level inventory:** `reservation-service` (hiện chỉ là skeleton 1 project `Api` từ Phase 1) được mổ xẻ đầy đủ 4 tầng Clean Architecture như catalog/identity. Sinh `Seat` cá thể từ section + capacity.

- **Polyglot persistence trong CÙNG 1 service (điểm học Ch8 mới):** khác Phase 1 (mỗi service 1 Postgres), reservation dùng **2 store cho 2 vai trò khác nhau** — đúng bảng scope mục 4 (DB chính = Redis, phụ = Postgres):
  - **Redis** = nguồn sự thật cho *"ghế còn trống ngay lúc này"*: hold (TTL 10'), sold-set, danh sách seatId hợp lệ. Đây là fast-path, atomic.
  - **Postgres** (`ticketflow_reservation`) = bản ghi **bền vững**: định nghĩa seat inventory + bản ghi ghế đã bán (sold) để audit/rebuild. Nếu Redis mất dữ liệu, Postgres là nguồn dựng lại.

- **Chống overselling = Lua script atomic, KHÔNG phải RedLock** — đây là chỗ tôi **phản biện scope.md** (mục 5 & Phase 2 ghi "Redis distributed lock (RedLock)"):
  - **RedLock** cho một *named mutex* (khoá loại trừ lẫn nhau). Nếu dùng RedLock kiểu `acquire lock:event:{eventId}` → check → set hold → release, thì **mọi** request giữ ghế của cùng 1 event bị **tuần tự hoá qua 1 khoá** — đúng về correctness nhưng **giết throughput** đúng lúc cần nhất (flash sale). Sai công cụ cho bài toán.
  - **Lua script** chạy **atomic** trên Redis (single-threaded execution): 1 script "check tất cả ghế yêu cầu có trống không, nếu trống thì set hold tất cả (all-or-nothing)" — chỉ khoá đúng những ghế đó, không tuần tự hoá cả event. Vừa đúng vừa nhanh. Bản chất chống double-hold nằm ở `SET NX` (set-if-not-exists) atomic.
  - **Kết luận:** dùng **StackExchange.Redis + Lua script** làm primitive chính. Vẫn ghi chú RedLock.net là công cụ đúng cho *tình huống khác* (khoá loại trừ xuyên nhiều thao tác độc lập, Redis multi-master) — để hiểu **khi nào** mới cần nó, không dùng bừa. Xem lại scope.md mục 5 nên sửa "RedLock" → "atomic Lua / SET NX (RedLock chỉ khi cần mutex xuyên thao tác)".

- **Hold sống HOÀN TOÀN trong Redis (ephemeral), TTL = auto-expire:** hold không ghi vào Postgres. Hết TTL → key tự biến mất → ghế tự trống lại. **Không cần background worker** để dọn hold hết hạn (item 3 "xử lý hết hạn hold tự động" được Redis TTL lo miễn phí). Nếu Phase sau cần *phản ứng* với sự kiện "hold hết hạn" (ví dụ notify), dùng **Redis keyspace notifications** — nhưng đó là hook Phase 3+, Phase 2 không cần vì availability tự đúng khi key mất.

- **Reservation domain CỐ TÌNH mỏng:** khác catalog (aggregate giàu behavior), consistency của reservation **do Redis atomicity đảm bảo**, không phải do load 1 aggregate vào bộ nhớ rồi enforce invariant. `Seat` gần như chỉ là record + `SeatStatus`. Logic distributed-systems thật nằm ở **Infrastructure** (Redis hold store + Lua script), không phải Domain. Đây là quan sát kiến trúc thật: **không phải bounded context nào cũng domain-rich** — có cái nặng về điều phối/hạ tầng. Ghi rõ để không gượng ép nhét behavior giả vào `Seat`.

- **Confirm (Held→Sold) chưa phải distributed transaction đầy đủ:** confirm ghi Postgres (durable) + cập nhật Redis sold-set. Hai bước này **chưa** atomic xuyên Redis+Postgres — đó là địa hạt của Saga/Outbox (**Phase 4/5**). Chấp nhận được ở Phase 2 vì **chỉ người đang giữ hold độc quyền** mới confirm được, không có tranh chấp lúc confirm; nếu crash giữa chừng, Postgres là nguồn sự thật, Redis rebuild được.

- **Self-provision inventory (chưa event-driven):** Phase 2 chưa có message bus (Phase 3). Nên reservation tự nạp seat inventory qua **REST endpoint nội bộ** (`POST /events/{eventId}/inventory` nhận sections + số ghế). Việc *tự động* provision khi catalog publish event (`EventPublished` → provision seats) là **Phase 3** (event-driven). **Không** cho reservation đọc thẳng DB catalog (phá DB-per-service), cũng chưa thêm gRPC catalog→reservation (tránh scope creep — chỉ thêm khi thật cần).

- **Redis client để ở reservation Infrastructure, chưa lên BuildingBlocks:** hiện chỉ reservation dùng Redis. Khi service thứ 2 cần (cache/pubsub — Phase sau) mới trích lên `BuildingBlocks`. Tránh vàng hoá.

- **Ports:** giữ nguyên như đã dựng — reservation REST 8003, gRPC 9003 (Kestrel 2 cổng đã cấu hình từ Phase 1 Commit 14). DB `ticketflow_reservation`. Redis đã có sẵn trong `docker-compose.infra.yml` từ Phase 0 (6379) — không cần đổi infra.

- **Kế thừa mọi convention Phase 1:** Central Package Management (`Directory.Packages.props`), native OpenAPI + Scalar, EF Core snake_case + `Ignore(DomainEvents)` + constructor rỗng cho EF, `Guard`/`ValueObject` từ BuildingBlocks, exception middleware map lỗi nghiệp vụ → 4xx.

---

## Nhóm A — Seat inventory: domain + Postgres (bản bền vững)

### Commit 1 — `feat(reservation): model Seat + SeatStatus (thin domain)`
- Mổ `reservation` từ skeleton thành có tầng `Domain`. Tạo project `TicketFlow.Reservation.Domain` (ref `TicketFlow.Domain`).
- `Seat : Entity<Guid>` { `EventId`, `SectionId`, `Label` (ví dụ `VIP-12`), `Status` }. `SeatStatus` enum (Available/Held/Sold).
- (Tuỳ) `SeatInventory` — factory sinh danh sách `Seat` từ (eventId, sectionId, sectionName, capacity): tạo `capacity` ghế nhãn `{sectionName}-{1..n}`.
- **Ghi rõ trong code/doc:** domain mỏng có chủ đích — consistency do Redis lo (xem quyết định kiến trúc), `Seat` không mang behavior tranh chấp.
- **Ch2/Ch8:** vẫn giữ `Seat` là entity định danh, nhưng thừa nhận đây là bounded context infra-heavy.

### Commit 2 — `feat(reservation): application layer — ports, DTOs, services`
- Project `TicketFlow.Reservation.Application` (ref Domain).
- Ports: `ISeatRepository` (Postgres — inventory + sold), `ISeatHoldStore` (Redis — hold/availability/confirm/release, **abstraction, impl ở Nhóm B**), `IUnitOfWork`.
- `InventoryService` (provision seats cho event), `ReservationService` (hold/release/confirm/query availability — orchestrate 2 store).
- DTOs: `ProvisionInventoryRequest`, `HoldSeatsRequest/Response` (trả `holdId` + `expiresAt` hoặc danh sách ghế hết chỗ), `Confirm/ReleaseRequest`, `SeatAvailabilityResponse`.
- **Ch2 (application boundary):** service điều phối Postgres ↔ Redis; đây là nơi "orchestrate polyglot", không phải trong domain.

### Commit 3 — `feat(reservation): EF Core + Postgres seat inventory (Ch8)`
- Project `TicketFlow.Reservation.Infrastructure` (ref Application; `Npgsql.EntityFrameworkCore.PostgreSQL`, EFCore.NamingConventions...).
- `ReservationDbContext` (schema `reservation`, DB `ticketflow_reservation`), `DbSet<Seat>`, config (index `(EventId, SectionId)`, unique `(EventId, Label)`), `SeatRepository`, `UnitOfWork`.
- Migration `InitialReservation` + startup migrate (dev-only, đúng pattern catalog/identity). Nhớ `Ignore(DomainEvents)` + constructor rỗng cho EF (bài học Phase 1).
- **Ch8:** DB thứ 3 tách biệt — verify `ticketflow_reservation` đứng riêng cạnh `_catalog`/`_identity`.

---

## Nhóm B — Redis holds: trái tim chống overselling

### Commit 4 — `feat(reservation): Redis connection + hold store wiring`
- Thêm `StackExchange.Redis`. Đăng ký `IConnectionMultiplexer` (singleton) đọc `Redis:ConnectionString` từ config (`redis:6379` trong Docker, `localhost:6379` local).
- Khung `RedisSeatHoldStore : ISeatHoldStore` — `LoadInventoryAsync` implement thật (không tranh chấp), 4 method còn lại `NotImplementedException` ghi rõ commit nào làm.
- `RedisHealthCheck` tự viết bằng `PingAsync()` sẵn có trong `StackExchange.Redis` — **không** thêm package `AspNetCore.HealthChecks.Redis` riêng cho 1 check.
- **Bug thật gặp:** `IHealthCheck`/`HealthCheckResult` nằm trong ASP.NET Core shared framework, không phải BCL — `Reservation.Infrastructure` là classlib thường nên thiếu reference (`CS0246`). Sửa bằng `<FrameworkReference Include="Microsoft.AspNetCore.App" />` (đúng pattern `TicketFlow.Web`). Hệ quả: `Microsoft.Extensions.Configuration.Abstractions` PackageReference cũ thành dư thừa (`NU1510`) — xoá.
- **Quyết định:** `ISeatHoldStore` giờ có implementation nên kỹ thuật đủ điều kiện gọi `AddReservationApplication()`, nhưng **cố tình chưa gọi** trong `Program.cs` — để dành Commit 7 cho nhất quán với Catalog (Application wire cùng lúc REST endpoint dùng nó).

### Commit 5 — `feat(reservation): atomic multi-seat hold via Lua (anti-oversell)` ⭐
- **Đây là commit lõi của cả phase.** Lua script (`HoldSeatsScript.cs`) all-or-nothing:
  - `KEYS[1]`=seats set, `KEYS[2]`=sold set, `KEYS[3]`=`holdseats:{holdId}` (reverse-index, xem Commit 6), `KEYS[4..]`=hold key mỗi ghế. `ARGV[1]`=holdId, `ARGV[2]`=ttl giây, `ARGV[3..]`=seatId song song với `KEYS[4..]`.
  - Với mỗi seat: kiểm tra `seat ∈ seats set` (hợp lệ) **AND** `seat ∉ sold set` **AND** không có key `hold:{eventId}:{seatId}`.
  - Nếu **tất cả** trống → `SET hold key = holdId EX ttl` + `SADD` vào `holdseats:{holdId}` cho từng ghế, trả `{}` rỗng.
  - Nếu **bất kỳ** ghế nào bận → **không set gì cả**, trả mảng seatId hết chỗ.
- Atomic vì Redis chạy Lua single-threaded → giữa check và set không ai chen được → **đúng 1 request thắng** mỗi ghế.
- `TryHoldAsync` (C#) build `RedisKey[]`/`RedisValue[]` động theo số ghế, gọi `IDatabase.ScriptEvaluateAsync` (không dùng `LuaScript.Prepare` vì đó hợp cho script cố định tham số, không hợp cho KEYS/ARGV dài động theo số ghế).
- **Ch8/Redis:** `SET`/`SISMEMBER`/`EXISTS` trong 1 Lua atomic là bản chất chống double-hold. So sánh với RedLock (xem quyết định kiến trúc) — RedLock là mutex đặt tên, sẽ tuần tự hoá cả event; ở đây chỉ khoá đúng những ghế liên quan.
- **Verify runtime thật** (script tạm gọi `RedisSeatHoldStore` qua Redis thật — không chỉ build): hold ghế trống → thành công; hold lại đúng ghế đó (holder khác) → đúng 1 bị từ chối; hold 2 ghế độc lập → cả 2 thành công; all-or-nothing với 1 ghế bận + 1 ghế không tồn tại → không set gì, cả 2 trả "unavailable"; **50 request đồng thời tranh đúng 1 ghế → đúng 1 người thắng** (bằng chứng atomicity, bản thu nhỏ của DoD chính — Commit 10 lặp lại ở quy mô 1000 bằng Testcontainers, không phải lần đầu tiên được chứng minh).

### Commit 6 — `feat(reservation): confirm, release & availability query`
- **Lỗ hổng thật phát hiện trước khi code Commit 6, phải quay lại sửa Commit 5:** `ReleaseAsync`/`ConfirmAsync` trong `ISeatHoldStore` chỉ nhận `(eventId, holdId)` — nhưng để biết xoá/xác nhận đúng key `hold:{eventId}:{seatId}` nào thì cần biết **ghế nào** thuộc về `holdId` đó, và không có cách nào tra ngược hiệu quả trong Redis nếu không có index. Sửa `HoldSeatsScript` (Commit 5) để ghi thêm 1 set `holdseats:{holdId}` (danh sách seatId thuộc hold đó) **atomic cùng lúc** với việc set các `hold:*` key, cùng TTL. Đây là ví dụ thực tế: thiết kế API trước (`ISeatHoldStore`, Commit 2) đôi khi bỏ sót chi tiết hiện thực hoá, phải quay lại sửa lớp dưới khi code lớp trên.
- **Release** (`ReleaseHoldScript`): đọc `SMEMBERS holdseats:{holdId}` → với mỗi seatId, `GET hold:{eventId}:{seatId}` — nếu đúng bằng `holdId` mới `DEL` (chỉ chủ hold mới nhả được, tránh nhả nhầm hold người khác đã chiếm lại ghế sau khi hold cũ hết hạn) → xoá luôn `holdseats:{holdId}`. Trả về số ghế đã nhả.
- **Confirm** (`ConfirmHoldScript`): cùng cấu trúc, nhưng thay vì `DEL` thì `SADD` seatId vào `sold:{eventId}` trước khi `DEL` hold key. Trả về danh sách seatId đã confirm — `ReservationService.ConfirmAsync` (Commit 2) dùng đúng danh sách này để `MarkSold()` + `SaveChanges` vào Postgres.
- **Đơn giản hoá có ghi chú:** cả 2 script tự dựng chuỗi `'hold:' .. event_id .. ':' .. seat_id` **bên trong Lua** thay vì truyền q qua `KEYS[]` từ C# — không an toàn cho Redis Cluster (cluster bắt buộc khai báo hết key chạm tới để định tuyến hash-slot đúng), nhưng chấp nhận được vì cả project chỉ chạy Redis **1 node** (`docker-compose.infra.yml`), và tránh phải tốn 1 round-trip đọc `SMEMBERS` từ C# trước khi gọi script.
- **Availability query** (`GetLiveStatusesAsync`): không cần Lua (không có yêu cầu atomic — đọc lệch nhau vài mili-giây giữa các ghế chấp nhận được cho hiển thị). Check `sold` bằng `SetContainsAsync` dạng bulk (1 round-trip); check `hold:*` bằng nhiều `KeyExistsAsync` chạy song song — `StackExchange.Redis` tự pipeline các lệnh gọi đồng thời trên cùng multiplexed connection nên vẫn chỉ tốn ~1 round-trip thực tế, không phải N.
- **Auto-expire:** không code gì thêm — Redis TTL tự lo, kể cả `holdseats:{holdId}` (có `EXPIRE` khớp TTL). Ghi chú keyspace notifications là hook Phase 3+.
- **Verify runtime thật** (script tạm gọi `RedisSeatHoldStore` qua Redis thật, không phải chỉ build): release nhầm `holdId` → không có tác dụng; release đúng `holdId` → ghế về Available; confirm 2 ghế → cả 2 thành Sold, list trả về đúng; confirm lại lần 2 cùng `holdId` → no-op (rỗng); không hold lại được ghế đã Sold; TTL 2 giây → sau 4 giây tự về Available không cần background job.

---

## Nhóm C — Phơi ra ngoài + nối dây

### Commit 7 — `feat(reservation): REST endpoints (provision, hold, release, confirm, availability)`
- Viết lại `Reservation.Api/Program.cs`: giờ mới gọi `AddReservationApplication()` (hoãn từ Commit 4, đúng như dự định — wire cùng lúc REST endpoint dùng nó), `AddOpenApi()` + `MapScalarApiReference()` (native OpenAPI/Scalar, đúng convention Phase 1), migrate lúc khởi động (giữ nguyên từ Commit 3).
- Endpoints (chưa qua gateway — route `/reservation/**` là Commit 9):
  - `POST /events/{eventId}/inventory` — body chỉ có `Sections` (không lặp lại `eventId` trong JSON, lấy từ route, tránh case route/body lệch nhau).
  - `GET /events/{eventId}/availability`.
  - `POST /holds` — body: `eventId`, `seatIds` (**không có `holderId`**, đúng quyết định Commit 2) → 200 + holdId/expiresAt hoặc **409** + `unavailableSeatIds`.
  - `POST /holds/{holdId}/confirm` → 200 / 404 (hold hết hạn/không tồn tại). `POST /holds/{holdId}/release` → 204 / 404.
  - **Thiết kế:** `confirm`/`release` cần thêm `eventId` trong body dù `holdId` đã ở route — vì Redis key scheme (`hold:{eventId}:{seatId}`) cần `eventId` để dựng đúng key, và không có cách suy ra `eventId` chỉ từ `holdId`. Client vốn đã biết `eventId` (đang mua vé sự kiện đó) nên không phải gánh nặng thêm.
- **Ch3:** service phơi REST — khách "chọn ghế" gọi thẳng reservation, đúng mục 7 scope: `U->>R: Chọn ghế`.
- **Verify runtime thật** (Postgres + Redis thật qua Docker, không chỉ build): provision 5 ghế → availability đúng 5 `Available` → hold 2 ghế → 200 → hold lại đúng 1 trong 2 ghế đó (holder khác) → **409** đúng `unavailableSeatIds` → availability giữa chừng thấy đúng `Held` → confirm → 200, ghế thành `Sold` **cả Redis lẫn Postgres** (`psql` xác nhận durable) → confirm lại lần 2 → **404** (đã dùng hết) → hold ghế khác rồi release → **204** → về lại `Available`. Regression: catalog/identity/ordering qua gateway vẫn 200, 35/35 test Catalog vẫn xanh.

### Commit 8 — `feat(reservation): gRPC CheckSeatAvailability phản ánh state thật; ordering thấy state thật`
- Thay implementation gRPC hardcoded `all_available: true` (Phase 1 Commit 14) bằng **query availability thật** — gọi `ReservationService.CheckAvailabilityAsync` (Application, mới thêm) chứ không tái dùng thẳng `GetAvailabilityAsync` (đó trả *toàn bộ* ghế của event, không phải đúng những ghế caller hỏi).
- **Bug thật cân nhắc trước khi code, không phải phát hiện sau:** `GetLiveStatusesAsync` (Redis) không hề check ghế có tồn tại trong `seats:{eventId}` hay không — với `GetAvailabilityAsync` (nguồn seatId luôn lấy từ Postgres trước) thì không sao, nhưng nếu `ordering` gửi 1 seatId bịa (không hề provision) qua gRPC, Redis sẽ báo "Available" (sai). Sửa bằng cách `CheckAvailabilityAsync` luôn `GetByIdsAsync` (Postgres) trước — seatId nào **không tìm thấy** thì coi là unavailable, mới hỏi Redis cho những seatId hợp lệ còn lại.
- **Bug thật gặp lúc code:** class gRPC generated từ `.proto` (`service ReservationService`) và class Application (`ReservationService`, namespace `...Application.Seats`) **trùng tên** — cả hai cùng vào scope trong `ReservationServiceImpl.cs` (1 để kế thừa `.ReservationServiceBase`, 1 để inject) → ambiguous reference. Sửa bằng `using ApplicationReservationService = TicketFlow.Reservation.Application.Seats.ReservationService;`.
- `ordering` `POST /check-seats` (Phase 1 Commit 15) giờ trả kết quả thật, không đổi code phía ordering.
- (Chưa thêm ConfirmSeats gRPC cho saga — đó là Phase 5. Giữ Phase 2 gọn.)
- **Verify runtime thật** (rebuild Docker, gọi qua `ordering` → gRPC → `reservation`, không chỉ build): 1 ghế Available → `all_available:true`; 1 ghế đã Sold (từ Commit 7) + 1 ghế Available → `all_available:false`, đúng `unavailableSeatIds`; 1 seatId bịa (chưa từng provision) → đúng bị coi unavailable (xác nhận guard ở trên thực sự cần thiết, không phải phòng thủ thừa). Regression: catalog/identity qua gateway + reservation REST vẫn 200, 35/35 test Catalog xanh.

### Commit 9 — `feat(reservation): dockerize with Redis + Postgres`
- `Dockerfile` + `ConnectionStrings__Default`/`Redis__ConnectionString` qua `appsettings.json` (Docker) đã có sẵn từ Commit 3/4 (verify runtime dọc theo mỗi commit từ đó tới giờ luôn build lại image) — Commit 9 chỉ còn thiếu 2 việc: `depends_on` trong compose và route gateway.
- `docker-compose.yml`: `reservation` thêm `depends_on: postgres (healthy) + redis (healthy)`; `gateway` thêm `reservation` vào `depends_on`.
- Gateway: route `reservation-route` (`/reservation/{**catch-all}` → strip prefix → `http://reservation:8003`, đúng pattern PathRemovePrefix của catalog/identity/ordering) trong `appsettings.json`; override `http://localhost:8003` trong `appsettings.Development.json` cho local dev (reservation không đổi port giữa Docker/local vì `Program.cs` tự cấu hình Kestrel qua `Kestrel:HttpPort`/`Kestrel:GrpcPort`, không dùng `ASPNETCORE_URLS` như các service khác — nên cả 2 môi trường đều là `:8003`).
- **Không route gRPC (9003) qua gateway** — gateway chỉ là REST reverse-proxy (YARP), giao tiếp gRPC nội bộ ordering→reservation đi thẳng qua Docker network, không qua gateway (đúng thiết kế mục 3 scope.md: gateway chỉ nằm giữa client và service layer).
- **Verify runtime thật đầy đủ** (`docker compose up -d --build` toàn stack, không chỉ reservation riêng lẻ): 9 container Up; provision + availability qua `http://localhost:8080/reservation/**` lần đầu tiên (trước giờ luôn test thẳng port 8003); xác nhận data cũ (từ Commit 7/8, tạo trước khi thêm route) **vẫn còn nguyên** sau khi rebuild toàn bộ — Postgres/Redis volume không mất; **vòng đời đầy đủ qua gateway**: hold → confirm → `ordering` (gRPC) thấy ngay ghế vừa confirm là `Sold` (`all_available:false`) — chứng minh tính nhất quán real-time xuyên suốt cả chuỗi Client→Gateway→Reservation→(Redis+Postgres) và Gateway→Ordering→gRPC→Reservation. Regression: catalog/identity/ordering qua gateway vẫn 200, 35/35 test Catalog xanh.

---

## Nhóm D — Chứng minh DoD (điểm nhấn của phase)

### Commit 10 — `test(reservation): Testcontainers integration test — 1000 concurrent holds, exactly 1 wins` ⭐
- **Đây là bằng chứng correctness thật của DoD** (không phải throughput). Project `TicketFlow.Reservation.IntegrationTests` (xUnit + `Testcontainers.Redis` — Redis THẬT trong container, đúng scope mục 11).
- `RedisFixture` (`IAsyncLifetime` + `IClassFixture`) — 1 container Redis dùng chung cho cả class test (khởi động container mỗi `[Fact]` sẽ chậm không cần thiết); cô lập giữa các test bằng GUID `eventId`/`seatId` mới mỗi lần, không phải reset container.
- Test ở tầng `RedisSeatHoldStore` (Infrastructure) trực tiếp, **không** qua `ReservationService`/`InventoryService` (Application) — vì `ProvisionAsync` cần Postgres, còn cơ chế chống-oversell nằm hoàn toàn ở Redis; đúng phạm vi gói `Testcontainers.Redis` (không kèm Postgres) mà plan đã chọn.
- Test chính: provision 1 ghế → `Task.WhenAll` 1000 hold request đồng thời **cùng ghế đó** → assert **đúng 1 success (unavailable rỗng), 999 fail (unavailable chứa đúng seatId)**.
- Test phụ: all-or-nothing (1 trong 2 ghế đã bận → cả lô fail, ghế còn lại **không** bị hold ngoài ý muốn); release trả ghế về Available; confirm chuyển Sold **và** chặn hold lại; TTL 2 giây hết hạn tự động không cần background job.
- **Scope mục 11:** integration test với Testcontainers — lần đầu tiên dùng trong dự án.
- **Verify:** `dotnet test` thật (Docker tự pull + khởi động container `redis:7`, không mock) — **5/5 pass trong 4 giây**, gồm cả bài test lõi 1000-concurrent. Đây là phiên bản **chính thức, chạy lại được, CI-friendly** của các lần verify thủ công bằng script tạm đã làm ở Commit 5/6 (50 request lúc đó, giờ nâng lên đúng 1000 theo DoD và biến thành test thường trực trong repo).

### Commit 11 — `test(reservation): NBomber load test — flash-sale throughput (companion)`
- Project `TicketFlow.Reservation.LoadTests` (console app, `NBomber` — .NET, ở trong solution, CI-friendly; k6 là lựa chọn thay thế nếu thích JS thuần).
- **Khác Commit 10 ở tầng đo:** Commit 10 gọi thẳng `RedisSeatHoldStore` in-process (nhanh, deterministic). Commit 11 gọi qua **HTTP thật** vào `Reservation.Api` đang chạy (mặc định `http://localhost:8003`, đổi được qua env `RESERVATION_BASE_URL`) — đo được cả tầng ASP.NET Core/JSON/network mà Commit 10 không chạm tới.
- Kịch bản: provision pool 100 ghế → 1000 lần gọi trong 20s (50 req/s, tự oversubscribe 10x để đảm bảo tranh chấp thật) → mỗi lần chọn ngẫu nhiên 1 ghế trong pool, `POST /holds`, nếu thành công thì `POST confirm` ngay. Sau khi chạy xong, gọi lại `GET availability`, đếm `Sold`, in PASS/FAIL nếu `sold > provisioned`.
- **Ghi rõ:** đây là **đo đặc tính tải**, không phải bằng chứng correctness — bằng chứng "đúng 1 thắng" là Commit 10. scope mục 11 & DoD gọi tên "k6/NBomber" nên vẫn làm để đúng cam kết.
- **Bug thật gặp & sửa:** `WithReportFolder("load-test-reports")` dùng path tương đối → phụ thuộc CWD lúc `dotnet run` (chạy từ repo root qua `--project` thì path resolve ra ngay **repo root**, không phải trong project) → rác thư mục report ra ngoài, không nằm trong `bin/` (vốn đã gitignore). Sửa bằng `Path.Combine(AppContext.BaseDirectory, "load-test-reports")` — luôn neo theo thư mục assembly build ra, bất kể chạy từ đâu.
- **Verify thật** (chạy 2 lần liên tiếp nhắm vào container `reservation` đang chạy sẵn, không mock): cả 2 lần đều **1000 request → đúng 100/100 ghế bán (khớp chính xác pool size), 900 bị từ chối 409** — không oversell, không undersell, lặp lại được. Latency thật qua HTTP: p50 ~2.4ms, p99 ~11-148ms (lần đầu có cold-start JIT). Report `.md`/`.html`/`.csv` nằm trong `bin/`. Regression: catalog/identity/reservation qua gateway vẫn 200, 35/35 test Catalog + 5/5 integration test Reservation vẫn xanh.

---

## Commit 12 — `chore: finalize Phase 2 — routes, DoD checklist`
- Add mọi project mới vào `TicketFlow.slnx`.
- Rà gateway routes (`/catalog`, `/identity`, `/ordering`, `/reservation`) + compose (postgres/redis healthy).
- Tick DoD Phase 2 trong `scope.md`; cập nhật doc này với các lệch kế hoạch/bug thực tế gặp khi code.

---

## Kiểm chứng DoD cuối Phase 2

| DoD | Kiểm chứng |
|---|---|
| 1000 request cùng 1 ghế → đúng 1 thắng | Testcontainers integration test (Commit 10): `Task.WhenAll` 1000 hold → assert 1 success + 999 "hết chỗ" |
| Không oversell ở quy mô tải | NBomber (Commit 11): tổng sold ≤ tổng inventory dưới tải đồng thời |
| Hold có TTL, tự hết hạn | Test: hold → chờ quá TTL → availability trả ghế trống lại (Redis TTL, không background job) |
| Reservation có DB riêng | psql: `ticketflow_reservation` tách biệt cạnh `_catalog`/`_identity`; hold sống ở Redis |

## Thứ tự đọc sách trước khi code (scope mục 13)
Đọc lại **Ch8 (Database strategies)** — trọng tâm phần **polyglot persistence** (Redis + Postgres trong 1 service) và **distributed data**. Bổ sung ngoài sách: đọc Redis docs về `SET NX EX`, `EVAL`/Lua atomicity, keyspace notifications; và tài liệu RedLock để hiểu **khi nào** cần mutex phân tán (để biết vì sao ở đây ta *không* cần nó).
