---
title: "Phase 5 — PurchaseSaga + Payment + Ticketing + Resiliency — Kế hoạch commit"
phase: 5
chapters: [Ch9 Saga, Ch10 Resiliency, MassTransit state machine, Polly]
created: 2026-07-14
tags: [ticketflow, phase-5, plan, saga, masstransit, polly, payment, ticketing, minio]
---

# Phase 5 — Kế hoạch chi tiết theo commit

> **Mục tiêu (từ scope mục 10):** luồng nghiệp vụ đinh (mua vé end-to-end) + chịu lỗi. Đây là nơi **Ch6/Ch7/Ch9/Ch10 hội tụ** — saga điều phối reservation → payment → ticketing → notification, có compensation khi lỗi.
> **DoD:** mua vé end-to-end thành công; **ép lỗi từng bước → hệ tự bù trừ (nhả ghế/hoàn tiền) đúng**.

> ⚠️ **Viết TRƯỚC khi code** (giống [phase-2](phase-2-plan.md)/[phase-3](phase-3-plan.md)/[phase-4-plan.md](phase-4-plan.md)). Version package + "lệch kế hoạch / bug tự bắt được" bổ sung dần khi thực thi. Đây là **phase lớn nhất** (~2.5 tuần, 11 commit) — nhiều mảnh ghép mới nhất từ đầu dự án.

---

## Bối cảnh: cái gì đã có, cái gì Phase 5 thêm

- **Đã có, sẽ được nối lại:**
  - `reservation` (Phase 2): hold/confirm/release ghế qua Redis Lua atomic + REST. Có sẵn `ReservationService.ConfirmAsync`/`ReleaseAsync` (Application) — Phase 5 **không viết lại logic Lua**, chỉ thêm **message consumer** gọi vào.
  - `ordering` (Phase 4): event-sourced qua Marten, có `Order` aggregate + `PlaceOrder`/read model `MyOrderView`. Phase 5 **mở rộng** Order với các milestone event của saga và biến `ordering` thành **host của PurchaseSaga**.
  - `BuildingBlocks.Messaging` (Phase 3): MassTransit + RabbitMQ + Outbox + `serviceName`-prefixed queue. Phase 5 dùng lại để saga gửi command / nhận event.
  - Marten (Phase 4) — dùng lại làm **saga state repository** (MassTransit.Marten), cùng Postgres với event store của ordering.
- **Phase 5 thêm mới:**
  - **MinIO** vào `docker-compose.infra.yml` (S3-compatible, lưu PDF vé).
  - `payment-service` (giả lập cổng thanh toán, **Polly** bọc lời gọi "cổng ngoài" giả) — DB `ticketflow_payment`.
  - `ticketing-service` (sinh QR + PDF → MinIO) — DB `ticketflow_ticketing`.
  - **PurchaseSaga** (MassTransit state machine) trong `ordering` — điều phối toàn luồng + compensation.
  - Message contracts mới (command + event của saga) vào `Contracts.Messaging`.

---

## Quyết định kiến trúc cho Phase 5

### 1. Orchestration saga (state machine), KHÔNG choreography (điểm học Ch9)
- **Orchestration:** 1 coordinator trung tâm (PurchaseSaga trong `ordering`) *ra lệnh* cho từng service và *phản ứng* với kết quả. Nhìn 1 chỗ là thấy toàn bộ luồng + mọi nhánh compensation.
- **Choreography** (đối trọng): mỗi service tự phản ứng với event, không có coordinator — phân tán, khó theo dõi compensation cho luồng nhiều bước. Scope chọn **orchestration** (mục 7 ghi "PurchaseSaga (MassTransit state machine)") — đúng cho luồng đinh phức tạp có bù trừ.
- Đây là điểm học Ch9 cốt lõi: saga = distributed transaction *không có* 2-phase-commit, thay bằng **chuỗi bước + bù trừ ngược khi lỗi**.

### 2. Giao tiếp saga ↔ participant = ASYNC command + event qua RabbitMQ, KHÔNG sync gRPC/REST
- Saga **gửi command** (`ConfirmSeats`, `ProcessPayment`, `IssueTickets`, và bù trừ `ReleaseSeats`, `RefundPayment`) → mỗi participant có **consumer** xử lý rồi **publish event kết quả** (`SeatsConfirmed`/`SeatsConfirmationFailed`, `PaymentProcessed`/`PaymentFailed`, `TicketsIssued`/`TicketingFailed`) → saga transition theo event.
- **Vì sao không sync (gRPC/REST) bên trong saga:** gọi sync khoá luồng, mất tính resilient (1 service chậm → saga treo), sai idiom MassTransit saga. Async command/event cho retry, redelivery, decoupling — đúng bản chất Ch9/Ch10.
- **Không đụng** gRPC `CheckSeatAvailability` (Phase 1/2) và REST hold (Phase 2) — chúng vẫn client-facing (khách chọn/xem ghế trước khi đặt). Saga dùng **đường message riêng**.
- **Hệ quả port:** `payment`/`ticketing` **không cần gRPC server** (khác bảng scope mục 4 ghi 9005/9006) — chỉ cần HTTP cho health + vài query endpoint + MassTransit consumer. Đề xuất bỏ cột gRPC của 2 service này khi reconcile scope.

### 3. Quan hệ Saga ↔ Order event-sourced (điểm tích hợp Ch7 + Ch9 — quyết định quan trọng nhất)
- **Saga instance** (`PurchaseState : SagaStateMachineInstance`) = **coordinator điều phối**, giữ `CurrentState` + dữ liệu tạm cần cho bù trừ (`HoldId`, `EventId`, `SeatIds`, `Amount`, `PaymentId`, `TicketPdfUrls`). Persist qua **Marten saga repository** (cùng Postgres với event store ordering). Correlated bằng **`OrderId`**.
- **Order aggregate** (Marten event stream, Phase 4) = **bản ghi nghiệp vụ bền vững**. Ở mỗi milestone thành công, saga **append domain event tương ứng vào stream của Order** (`SeatsConfirmed`, `PaymentAuthorized`, `TicketsIssued`, `OrderCompleted`; nhánh lỗi: `PaymentFailed`, `Refunded`, `SeatsReleased`, `OrderCancelled`) → Order phản ánh **đúng state diagram scope mục 7**, và `MyOrderView` read model tự cập nhật.
- **Phân vai rạch ròi:** saga lo *điều phối* (gửi lệnh, xử lý timeout/retry, giữ state tạm); Order event stream lo *lịch sử nghiệp vụ* (audit + read model). Khi saga kết thúc (Completed/Cancelled), MassTransit finalize saga instance (xoá), **nhưng Order event stream còn mãi** — đúng tinh thần "saga là nhạc trưởng nhất thời, event stream là bản ghi vĩnh viễn".
- **⚠️ Đây là chỗ Phase 4 tiến hoá:** `OrderConfirmed`/`OrderCancelled` thủ công của Phase 4 (Commit 3-4) được **thay bằng lifecycle do saga lái**. Endpoint confirm/cancel thủ công cũ: hoặc bỏ, hoặc giữ làm admin-override. Sẽ chốt cụ thể ở Commit 5.

### 4. `PlaceOrder` giờ KHỞI ĐỘNG saga (tích hợp với Phase 4)
- Luồng thật (scope mục 7): khách **hold ghế trước** (reservation REST, Phase 2 — nhận `holdId`), rồi **"bấm thanh toán"** → gọi `POST /ordering/orders` (PlaceOrder **mở rộng**: mang thêm `HoldId`, `Amount`, `PaymentMethod`).
- PlaceOrder → append `OrderPlaced` (mở rộng field) → publish `OrderSubmitted` event → saga `Initially(When(OrderSubmitted))` tạo instance, bắt đầu bước B1 `ConfirmSeats`.
- **Không** hold ghế bên trong saga — hold đã xong trước đó; saga chỉ *chốt* (confirm) hold đang giữ. Nếu hold hết TTL trước khi khách bấm thanh toán → Redis tự nhả (Phase 2) → `ConfirmSeats` fail → saga vào nhánh cancel. Đây là 1 trong 3 kịch bản lỗi của DoD, **không cần code mới** để "nhả hold hết hạn".

### 5. Resiliency = Polly bọc lời gọi "cổng ngoài" giả trong payment-service (điểm học Ch10)
- **Polly 8.x** (`ResiliencePipeline` API mới, free BSD-3). `payment-service` có 1 `FakePaymentGatewayClient` mô phỏng gọi cổng thanh toán ngoài (có thể ép fail/timeout/chập chờn theo input để test). Lời gọi này **bọc Polly**: **retry** (lỗi transient) + **circuit breaker** (cổng sập liên tục → mở mạch, fail nhanh) + **timeout**.
- Đây là nơi Ch10 sống thật: không phải bọc Polly khắp nơi, mà **đúng chỗ ranh giới gọi hệ thống ngoài không đáng tin** (payment gateway). Ghi rõ để không rải Polly bừa.
- **Timeout của hold** (kịch bản lỗi thứ 3) do **Redis TTL** lo sẵn (Phase 2) — không phải Polly timeout. Phân biệt rõ 2 loại timeout.

### 6. Idempotency = bật MassTransit Inbox (nợ kỹ thuật từ Phase 3, giờ trả)
- Bus giao **at-least-once** → command có thể tới 2 lần. Giờ payment/ticketing **ghi state thật** (charge tiền, phát vé) → xử lý trùng = **charge 2 lần / phát 2 vé** = hậu quả thật, không như consumer chỉ-log của Phase 3.
- Bật **MassTransit EF Core/Marten Inbox** ở payment + ticketing (đúng như ghi chú "Hướng mở rộng" Phase 3 đã hẹn: *"bật inbox khi consumer bắt đầu ghi state"*). Cộng thêm **idempotency key nghiệp vụ** (dedup theo `OrderId`/`PaymentId`) để chắc chắn 2 lớp.
- Compensation cũng phải idempotent: `RefundPayment` gọi 2 lần chỉ hoàn 1 lần; `ReleaseSeats` gọi 2 lần chỉ nhả 1 lần (reservation Phase 2 đã idempotent sẵn — release nhầm holdId là no-op).

### 7. Ticketing: QR + PDF → MinIO
- **QR:** `QRCoder` (MIT, free) — mỗi ghế 1 QR chứa mã vé (ký/hash để chống giả, verify ở cổng — phần scan cổng là **Phase 8**, Phase 5 chỉ phát + lưu).
- **PDF:** `QuestPDF` — nuspec ghi MIT nhưng bản mới yêu cầu set `QuestPDF.Settings.License = LicenseType.Community` lúc khởi động (Community **free** cho phi-thương-mại/doanh thu <$1M — đúng dự án học). Ghi chú như đã làm cho MassTransit/MediatR: đây là package có điều khoản license cần biết, nhưng **hợp lệ & miễn phí** cho ngữ cảnh này. Xác minh setup lúc code (Commit 3).
- **MinIO:** SDK `Minio` 7.x (hoặc `AWSSDK.S3` cho tính S3-generic). Đề xuất **Minio SDK** cho trực tiếp. MinIO thêm vào infra (Commit 1). Ticketing upload PDF → trả về URL/object key, đính vào `TicketsIssued` event.

### 8. Ports + reconcile scope (cần chốt)
- **Va chạm thật:** Phase 3 đã lấy **8005** (notification) + **8006** (search), nhưng scope mục 4 gán payment=8005, ticketing=8006 → bảng port scope **đã lệch thực tế** từ Phase 3.
- **Đề xuất (chờ bạn duyệt ở Commit 11):** giữ nguyên notification 8005 / search 8006 (đang chạy), gán **payment 8007**, **ticketing 8008** (HTTP; không gRPC — xem mục 2). Reconcile bảng port scope 1 lần cho khớp thực tế. *(Đây là quyết định port thuần, sẽ hỏi lại khi finalize — mặc định plan đi theo đề xuất này.)*

### 9. Kế thừa & hạ tầng
- Central Package Management, native OpenAPI + Scalar (service có HTTP), EF Core snake_case (payment/ticketing dùng EF Core state-based — chúng **không** event-sourced, khác ordering), `Ignore(DomainEvents)` + ctor rỗng, `Guard`/`ValueObject`, exception middleware, health check, migrate-on-startup, cập nhật `.slnx` + Dockerfile + `docker-compose.yml` mỗi khi thêm project/service.
- **payment/ticketing state-based (EF Core), KHÔNG event-sourced** — chúng là service CRUD nghiệp vụ đơn giản (bản ghi charge, bản ghi vé); event sourcing chỉ đáng cho `ordering` (aggregate có lifecycle phức tạp). Tránh vàng hoá — không phải service nào cũng cần Marten.

---

## Nhóm A — Infra + participant skeletons + contracts

### Commit 1 — `feat(infra): MinIO + saga message contracts` *(làm bởi user, review lại)*
- `docker-compose.infra.yml`: thêm `minio` (API 9000 + console 9001, root user/pass, volume, healthcheck `mc ready local`). Bucket `tickets` **chưa tạo** ở commit này — để lazy-create trong ticketing lúc start (Commit 3), đúng 1 trong 2 phương án plan cho phép.
- `Contracts.Messaging`: đủ 16 file command/event dưới `Ordering/`, `Payment/`, `Reservation/`, `Ticketing/`, XML doc rõ ràng, đúng shape thiết kế.
- **🐛 2 vấn đề thật phát hiện khi review (trước khi làm Commit 2):**
  1. **`ConfirmSeats`/`ReleaseSeats` thiếu `EventId`** — `ReservationService.ConfirmAsync(Guid eventId, Guid holdId, ...)`/`ReleaseAsync(Guid eventId, Guid holdId, ...)` (Phase 2) **bắt buộc** `eventId` (Redis key scheme `hold:{eventId}:{seatId}`), nhưng 2 command mới chỉ có `OrderId`/`HoldId`/`SeatIds` — sẽ chặn cứng Commit 6 (reservation consumer) nếu không sửa. **Đã sửa ngay:** thêm `Guid EventId` vào cả 2 record.
  2. **`SeatIds` lệch kiểu giữa Order và Reservation** — `Order.SeatIds` (Ordering.Domain, Phase 4) là `IReadOnlyList<string>`, nhưng Reservation's `Seat` (Phase 2) có identity thật là `Guid` (`Seat : Entity<Guid>`, `HoldSeatsRequest.SeatIds: IReadOnlyList<Guid>`). Contract mới (`ConfirmSeats`/`ReleaseSeats`/`OrderSubmitted`/`IssueTickets`/`OrderCompleted`) dùng đúng `Guid` (khớp Reservation) — **đây là lựa chọn đúng**, nhưng có nghĩa **Commit 5** (mở rộng `PlaceOrderCommand`) sẽ phải quyết định: đổi `Order.SeatIds` sang `Guid`, hay parse qua lại ở ranh giới. Ghi chú trước, **chưa sửa ngay** (không thuộc phạm vi Commit 1/2) — quyết định cụ thể ở Commit 5.
- **Verify thật:** build toàn solution 0 Warning/0 Error (sau khi sửa #1). Docker Desktop rớt giữa chừng (đã quen từ các phase trước) → khởi động lại → `docker compose up -d` toàn stack → gặp `Conflict: container name "/ticketflow-minio" already in use` (container cũ còn sót từ lần chạy trước, exited) → `docker rm` rồi up lại → **13 container** (đúng, tính cả `minio` mới) Up, `minio` `(healthy)`. `curl :9001` (console) và `:9000/minio/health/live` (API) đều `200`. Full smoke test gateway 4/4 route vẫn `200` — không regression.

### Commit 2 — `feat(payment): service skeleton + Polly-wrapped fake gateway`
- **🐛 Bug thật gặp ngay khi viết test — class `Payment` va chạm namespace root:** project đặt tên `TicketFlow.Payment.*` (namespace root = "TicketFlow.Payment"), và định đặt tên aggregate là `Payment` — `Payment.Initiate(...)` trong 1 namespace khác (`TicketFlow.Payment.Domain.Tests.Payments`) bị compiler báo `CS0234 "Initiate does not exist in namespace TicketFlow.Payment"`, vì `Payment` bị hiểu nhầm thành segment namespace `TicketFlow.Payment` thay vì type. Khác hẳn `Order`≠`Ordering`/`Event`≠`Catalog` ở các phase trước (không trùng chữ) — ở đây tên khái niệm và tên service **trùng y hệt**. **Đổi tên aggregate → `PaymentTransaction`** — vừa tránh va chạm, vừa khớp đúng pattern "Order sống trong Ordering" đã dùng.
- `PaymentTransaction : Entity<Guid>` (không phải `AggregateRoot<Guid>` — không cần `Raise()`/`DomainEvents`, vì integration event publish thẳng từ kết quả gọi, không dịch qua domain event như Catalog). State machine: `Initiate`→`Pending`; `MarkSucceeded`/`MarkFailed` (chỉ từ `Pending`); `MarkRefunded` (chỉ từ `Succeeded`) — đủ guard cho từng nhánh, kể cả double-transition.
- `Money` VO riêng cho Payment (không tái dùng Catalog/Ordering — đúng nguyên tắc không cross-context-reference).
- **Port `IPaymentGatewayClient`** (Application) tách khỏi implementation — `FakePaymentGatewayClient` (Infrastructure) mô phỏng cổng ngoài, ép fail deterministic khi `Amount % 1.00m == 0.13m` (không random flaky).
- **Polly `ResiliencePipeline`** tách riêng thành `PaymentGatewayResiliencePipeline.Create()` (static factory, không giấu trong `ServiceCollectionExtensions`) — lý do: cho phép test **trực tiếp** hành vi retry/circuit-breaker bằng delegate giả lập tuỳ ý kiểm soát, thay vì chỉ quan sát gián tiếp qua `ChargeAsync` (vốn nuốt hết exception thành `GatewayChargeResult(false,...)`, không phân biệt được "hết retry" với "circuit đã mở"). Retry 3 lần backoff mũ; circuit breaker mở khi ≥50% trong ≥4 lời gọi gần nhất fail, đóng lại sau 15s; timeout 5s/lần gọi.
- Consumer stub `ProcessPaymentConsumer`/`RefundPaymentConsumer` — chỉ log, chưa gọi gateway/persist/publish (đúng dự định, Commit 7 mới nối dây thật).
- **Không hand-roll DB health check** — rà lại thấy Catalog/Identity cũng không có health check Postgres riêng (EF migrate-on-startup đã đủ báo lỗi kết nối lúc khởi động) — Payment theo đúng convention đó, không thêm thừa.
- **Verify thật (không mock), 3 lớp:**
  1. **Domain:** `TicketFlow.Payment.Domain.Tests` — 9 test (Initiate/MarkSucceeded/MarkFailed/MarkRefunded + toàn bộ nhánh guard double-transition). **9/9 pass.**
  2. **Polly pipeline (đúng yêu cầu "unit test Polly pipeline" của plan):** `TicketFlow.Payment.Infrastructure.Tests` (project mới, lần đầu có test cho tầng Infrastructure trong dự án) — gọi thẳng `PaymentGatewayResiliencePipeline.Create()` với delegate giả lập: (a) fail 2 lần rồi thành công → retry đưa về đúng kết quả cuối; (b) luôn fail → đúng 4 lần thử (1 gốc + 3 retry) rồi ném exception; (c) gọi lặp lại nhiều lần với delegate luôn fail → **`BrokenCircuitException` thật sự xuất hiện** trong vòng 8 lần gọi, xác nhận circuit breaker mở đúng ngưỡng bằng dữ liệu thật, không suy đoán. **3/3 pass** (~5s, do có backoff thật).
  3. **Docker + message bus thật:** build image → `docker compose up -d payment` → log sạch: migration `InitialPayment` áp dụng, **queue đúng prefix** `payment-process-payment`/`payment-refund-payment` (xác nhận fix Phase 3 Commit 6 — serviceName formatter — hoạt động đúng cho service mới, không tái diễn bug trùng tên) → `Bus started`. `psql` xác nhận bảng `payment.payment_transactions`. `/health` → `200 Healthy`.
  4. **End-to-end message thật** (scratch console app publish trực tiếp qua RabbitMQ thật, đã xoá sau khi xong): publish `ProcessPayment`+`RefundPayment` → `docker logs ticketflow-payment` xác nhận **cả 2 consumer nhận đúng dữ liệu** (đúng `OrderId`, `Amount`, `PaymentMethod`, `PaymentId`).
  5. Regression: `docker compose ps` đủ **13 container**; full smoke test gateway 4/4 route `200`; toàn bộ test suite cũ (`Catalog.Domain.Tests` 35, `Ordering.Domain.Tests` 8, `Ordering.Application.Tests` 10) vẫn xanh.

### Commit 3 — `feat(ticketing): service skeleton + QR/PDF → MinIO`
- Service mới `ticketing` (port **8008**, DB `ticketflow_ticketing`, EF Core, schema `ticketing`). `Ticket : Entity<Guid>` (OrderId, SeatId, QrPayload, PdfObjectKey, IssuedAt) — state-based như `PaymentTransaction`, không `AggregateRoot`. **Không va chạm tên namespace** như `Payment`/`TicketFlow.Payment` ở Commit 2 — `Ticket` ≠ `Ticketing`, build sạch ngay lần đầu.
- `ITicketPdfRenderer`/`TicketPdfModel` (Application, port) ghi rõ trong doc comment: PDF chỉ hiển thị **raw `EventId`/`SeatId`** (Guid), không có tên sự kiện/venue/số ghế thân thiện — Ticketing không có quyền truy cập dữ liệu Catalog. **Giới hạn thật, ghi vào backlog**, không phải bug — sẽ cần thêm dữ liệu snapshot vào `IssueTickets` command (từ saga) nếu muốn PDF đẹp hơn, để dành quyết định khi code Commit 5/8.
- `ITicketObjectStorage` (Application, port) → `MinioTicketObjectStorage` (Infrastructure): `UploadAsync` tự `EnsureBucketExistsAsync` (bucket `tickets` lazy-create đúng như Commit 1 để dành) rồi `PutObjectAsync`.
- **🐛 API Minio SDK 7.x không rõ ràng từ tài liệu — dò bằng reflection:** scaffold console app tham chiếu thẳng `Minio` 7.0.0, dùng `GetMethods`/`GetConstructors`/`AppDomain.CurrentDomain.GetAssemblies()` để tìm đúng namespace/API thật, vì `new MinioClient()` chỉ có constructor rỗng (không phải builder pattern như tài liệu cũ gợi ý). Kết quả: type nằm ở `Minio.DataModel.Args.*` (`BucketExistsArgs`, `MakeBucketArgs`, `PutObjectArgs`), và cách dựng client đúng là **extension method fluent trên `IMinioClient`** (`Minio.MinioClientExtensions`: `.WithEndpoint().WithCredentials().WithSSL().Build()`), đăng ký qua DI native `services.AddMinio(client => client.With...().Build())` (`Minio.ServiceCollectionExtensions`) — không phải factory thủ công.
- `QuestPdfTicketRenderer` (Infrastructure): `QRCoder` (`QRCodeGenerator` → `PngByteQRCode.GetGraphic(scale)`) sinh QR PNG mỗi ghế → `QuestPDF` compose trang A5 (header/order/event/seat/customer/issued/QR image/footer). `QuestPDF.Settings.License = LicenseType.Community` set 1 lần ở `Program.cs` lúc khởi động (đúng điều khoản Community — free cho dự án học, không phải vấn đề license NuGet).
- Consumer `IssueTicketsConsumer` — **stub, chỉ log**, chưa render/upload/persist/publish (đúng dự định, Commit 8 mới nối dây thật) — giống hệt split "skeleton trước, wire thật sau" đã dùng cho Payment Commit 2 → 7.
- Migration `InitialTicketing`: schema `ticketing`, bảng `tickets`, **unique index `(order_id, seat_id)`** tạo sẵn từ bây giờ — để dành cho idempotency dedup ở Commit 8 (phát vé trùng = no-op).
- `docker-compose.yml`: `ticketing` depends_on `postgres` + `rabbitmq` + `minio` (cả 3 healthy) — service đầu tiên phụ thuộc MinIO.
- **Verify thật, nhiều lớp:**
  1. **Domain:** `TicketFlow.Ticketing.Domain.Tests` — 4 test (`Issue` hợp lệ, `QrPayload`/`PdfObjectKey` rỗng ném exception, 2 vé cùng ghế khác `Id`). **4/4 pass.**
  2. **Docker + migration:** build image → `docker compose up -d ticketing` → log: migration `InitialTicketing` áp dụng schema `ticketing`, **queue đúng prefix** `ticketing-issue-tickets` (serviceName formatter hoạt động đúng, không tái diễn collision Phase 3 Commit 6) → `Bus started`. `/health` → `200 Healthy`.
  3. **Render + upload thật (scratch console app, đã xoá sau khi xong):** gọi thẳng `QuestPdfTicketRenderer.Render()` + `MinioTicketObjectStorage.UploadAsync()` — PDF sinh ra có magic bytes `%PDF-1.4` hợp lệ, upload thành công vào bucket `tickets`, xác nhận **độc lập qua `mc cat`** (MinIO CLI client, không chỉ tin SDK return value) — file tải về mở được, QR đọc đúng payload.
     - **Không phải bug production:** scratch script tự ghép `objectKey = "tickets/{orderId}/{seatId}.pdf"` trong khi bucket đã tên `tickets` sẵn → tạo path lồng `tickets/tickets/...` bên trong bucket. Phát hiện qua `mc ls local/tickets` thấy sub-entry `0B tickets/` lạ. **Bài học cho Commit 8:** khi ticketing thật sự ghép object key, không tự thêm tiền tố tên bucket vào key. Dọn dẹp object test qua `mc rm --recursive --force`.
  4. **Message consumer thật qua RabbitMQ (scratch publish, đã xoá sau khi xong):** publish `IssueTickets` thật vào bus → `docker logs ticketflow-ticketing` xác nhận **consumer nhận đúng dữ liệu** (`OrderId`, số ghế, `CustomerName`), queue `ticketing-issue-tickets` — cùng kiểu verify "publish thật, đọc log thật" đã dùng cho Payment Commit 2.
  5. **Sự cố không liên quan (ghi nhận, không phải bug):** giữa lúc verify, toàn bộ 10 container non-dependency rơi vào `Exited (255)` cùng lúc — tái diễn pattern Docker Desktop tự khởi động lại nền đã gặp nhiều lần trong session — khắc phục bằng `docker compose up -d` full stack (không chỉ `ticketing`).
  6. **Regression cuối:** **14 container** Up (đủ payment + ticketing + minio); full smoke test gateway 4/4 route `200`; `payment`/`ticketing` `/health` đều `200`; toàn bộ test suite cũ vẫn xanh — `Ticketing.Domain.Tests` 4/4, `Payment.Domain.Tests` 9/9, `Payment.Infrastructure.Tests` 3/3, `Catalog.Domain.Tests` 35/35, `Ordering.Domain.Tests` 8/8, `Ordering.Application.Tests` 10/10 (**69/69 tổng, 0 regression**).

---

## Nhóm B — PurchaseSaga (trái tim Phase 5)

### Commit 4 — `feat(ordering): PurchaseSaga state machine (happy path)`
- `PurchaseState : SagaStateMachineInstance` (`TicketFlow.Ordering.Application/Sagas/PurchaseState.cs`) — CorrelationId=OrderId, CurrentState, UserId, CatalogEventId, HoldId, SeatIds, Amount, PaymentMethod, CustomerName, CustomerEmail, PaymentId, TicketPdfUrls, SubmittedAt. **Plain public get/set properties**, khác hẳn convention Entity/AggregateRoot (private-set + factory) dùng khắp dự án — lý do: MassTransit's saga repository (ở đây MassTransit.Marten) tự đọc/ghi trực tiếp qua serialization ở mỗi transition, không có domain behavior nào cần bảo vệ ở đây, chỉ là dữ liệu điều phối tạm thời. Ghi rõ trong doc comment để không ai "sửa lại cho giống Entity" sau này.
- `PurchaseSaga : MassTransitStateMachine<PurchaseState>` (cùng thư mục) — happy path đúng plan: `OrderSubmitted` → publish `ConfirmSeats` → `AwaitingSeats` → on `SeatsConfirmed` → publish `ProcessPayment` → `AwaitingPayment` → on `PaymentProcessed` → publish `IssueTickets` → `AwaitingTickets` → on `TicketsIssued` → publish `OrderCompleted` + `NotifyOrderCompleted` → `Finalize()` + `SetCompletedWhenFinalized()`.
- **Quyết định thật: dùng `Publish` cho cả 3 command (ConfirmSeats/ProcessPayment/IssueTickets), không `Send`.** Lý do ghi trong doc comment của `PurchaseSaga`: `Send` cần biết URI hàng đợi đích (`queue:{serviceName}-{kebab-consumer-name}`), tức saga phải hardcode đúng convention formatter của **assembly khác** (`TicketFlow.Messaging`) — giòn, dễ lệch khi 1 bên đổi. Vì mỗi command chỉ có **đúng 1 consumer** bind vào exchange của message type đó (qua `ConfigureEndpoints`), `Publish` định tuyến y hệt `Send` mà không cần biết tên hàng đợi. Xác nhận đúng bằng thực nghiệm (xem mục Verify).
- **🐛 2 vấn đề contract phát hiện khi code (giống class bug Commit 1):**
  1. **`OrderSubmitted` thiếu `CustomerName`/`CustomerEmail`** — saga không có nguồn dữ liệu nào khác để dựng `IssueTickets` (bắt buộc 2 field này) sau khi thanh toán xong, vì saga không gọi thẳng Identity/Catalog. **Đã sửa:** thêm 2 field vào `OrderSubmitted`. Giá trị thật (từ user đã đăng nhập) sẽ được điền ở Commit 5 khi `PlaceOrderCommand` publish event này thật.
  2. **`NotifyOrderCompleted` chưa tồn tại** — plan có nhắc tên nhưng chưa có contract. Đã tạo `Contracts.Messaging/Notification/NotifyOrderCompleted.cs` (OrderId, UserId, CustomerName, CustomerEmail, TicketPdfUrls). Saga publish vào "khoảng không" — chưa ai consume tới Commit 9, đúng thứ tự "contract trước, consumer sau" đã dùng suốt Phase 5.
- **Đặt tên né bug Commit 2:** 4 property `Event<T>` đặt tên có hậu tố `Event` (`OrderSubmittedEvent` thay vì `OrderSubmitted`) — tránh đúng loại va chạm tên đã gây bug thật ở Commit 2 (`Payment` trùng namespace `TicketFlow.Payment`), lần này là property trùng tên type message.
- **🐛 API `MassTransit.Marten` không rõ từ tài liệu — dò bằng reflection (giống Minio Commit 3):** NuGet package id `MassTransit.Marten` nhưng DLL thật tên `MassTransit.MartenIntegration.dll`. Ban đầu dùng overload `MartenRepository(connectionString)` — build cảnh báo **obsolete** (`CS0618`): bản mới khuyến nghị `MartenRepository()` không tham số, tái dùng thẳng `IDocumentStore` đã đăng ký qua `AddMarten(...)` sẵn có trong `AddOrderingInfrastructure` (Phase 4) — tránh tạo 2 `DocumentStore` trùng lặp cho cùng 1 Postgres. **Đã sửa dùng overload không tham số.**
- **Lần đầu Ordering có MassTransit** — trước Commit 4, `ordering` hoàn toàn chưa đụng message bus (chỉ Marten event store + gRPC client). Thêm `TicketFlow.Messaging` + `TicketFlow.Contracts.Messaging` reference vào `Ordering.Api`/`Ordering.Application`, `RabbitMq:*` config vào appsettings, `rabbitmq: service_healthy` vào `depends_on` của `docker-compose.yml`, cập nhật Dockerfile COPY layering cho 2 project mới tham chiếu.
- **Verify thật, 3 lớp:**
  1. **In-memory (`MassTransit.Testing.ITestHarness`, không RabbitMQ/Marten):** `PurchaseSagaTests.Happy_path_drives_saga_from_OrderSubmitted_to_finalized` — feed `OrderSubmitted` → assert publish đúng `ConfirmSeats` + state `AwaitingSeats` → feed `SeatsConfirmed` → assert `ProcessPayment` + `AwaitingPayment` → feed `PaymentProcessed` → assert `IssueTickets` (đúng `EventId`/`CustomerEmail` mang theo từ `OrderSubmitted`) + `AwaitingTickets` → feed `TicketsIssued` → assert `OrderCompleted` + `NotifyOrderCompleted` được publish, và saga đã finalize (`sagaHarness.Exists(...)` trả `null`). **11/11 pass** (10 test cũ + 1 mới, ~32s do harness khởi động thật).
  2. **Docker + RabbitMQ thật:** build image → deploy → log xác nhận `Configured endpoint ordering-purchase-state, Saga: PurchaseState, State Machine: PurchaseSaga` (tên hàng đợi lấy theo **tên saga instance type**, không phải tên state machine class — phát hiện thật khi đọc log) → `Bus started`. `/health` `200` (cả trực tiếp lẫn qua gateway).
  3. **End-to-end thật qua Postgres + RabbitMQ (scratch console app 4 bước, đã xoá sau khi xong):** publish `OrderSubmitted` thật → `psql` xác nhận **`mt_doc_purchasestate` được Marten tự tạo** (lazy, đúng `AutoCreate.CreateOrUpdate`) với `current_state = AwaitingSeats` → publish `SeatsConfirmed` → `psql` xác nhận `AwaitingPayment` → publish `PaymentProcessed` → `psql` xác nhận `AwaitingTickets` + `PaymentId` đã lưu đúng → publish `TicketsIssued` → `psql` xác nhận **row đã bị xoá** (0 rows — `SetCompletedWhenFinalized()` hoạt động đúng qua Marten thật, không chỉ trong test in-memory). **Phát hiện ngoài dự kiến:** ở bước `PaymentProcessed`, `docker logs ticketflow-ticketing` cho thấy **consumer `IssueTicketsConsumer` thật (đã deploy từ Commit 3) tự nhận đúng `IssueTickets`** do saga publish — xác nhận quyết định "Publish thay Send" hoạt động đúng **xuyên suốt hệ thống thật** (2 service độc lập, không chỉ trong 1 saga cô lập), không chỉ lý thuyết. Không log lỗi nào trong toàn bộ 4 bước.
  4. **Regression cuối:** **14 container** Up; gateway smoke test 4/4 route `200` (catalog/identity/reservation/ordering); toàn bộ test suite: `Ordering.Domain.Tests` 8/8, `Catalog.Domain.Tests` 35/35, `Payment.Domain.Tests` 9/9, `Ticketing.Domain.Tests` 4/4, `Payment.Infrastructure.Tests` 3/3, `Ordering.Application.Tests` 11/11 (**70/70 tổng, 0 regression**).

### Commit 5 — `feat(ordering): Order milestone events + PlaceOrder starts saga`
- **Quyết định chốt từ Commit 1 (SeatIds lệch kiểu): đổi `Order.SeatIds`/`OrderPlaced.SeatIds`/`PlaceOrderCommand.SeatIds`/`MyOrderView.SeatIds`/`OrderResponse.SeatIds` từ `IReadOnlyList<string>` sang `IReadOnlyList<Guid>`** — khớp đúng seat identity thật của Reservation (`Seat : Entity<Guid>`) mà `ConfirmSeats`/`SeatsConfirmed`/... (Contracts.Messaging, Commit 1) đã dùng. Chọn sửa tận gốc thay vì parse qua lại ở ranh giới — nhất quán với cách dự án luôn xử lý mismatch kiểu dữ liệu (Commit 1's EventId, Commit 4's OrderSubmitted). *Không đụng* `CheckSeatsRequest`/`CheckSeatsResponse` (`string`, theo đúng `.proto` gRPC của Reservation, Phase 1/2) — khác concern, để riêng.
- Mở rộng `Order` với 3 milestone mới + tái dùng `OrderConfirmed` làm milestone cuối:
  - `OrderStatus` thêm `SeatsConfirmed`/`PaymentAuthorized`/`TicketsIssued` (giữa `Pending` và `Confirmed`).
  - 3 record mới: `OrderSeatsConfirmed`, `OrderPaymentAuthorized(PaymentId)`, `OrderTicketsIssued(TicketPdfUrls)` + `Apply` tương ứng trong `Order.cs` và `MyOrdersProjection.cs`.
  - **Đặt tên có tiền tố `Order` (không phải `SeatsConfirmed`/`TicketsIssued` trơn)** — né đúng loại va chạm đã gây bug ở Commit 2 (`Payment`/namespace) và đã né ở Commit 4 (`Event<T>` suffix): `Contracts.Messaging.Reservation.SeatsConfirmed` và `Contracts.Messaging.Ticketing.TicketsIssued` đã tồn tại, code milestone-consumer cần `using` cả 2 namespace cùng lúc.
  - **Milestone cuối cùng KHÔNG tạo `OrderCompleted` domain event mới** — tên đó đã bị `Contracts.Messaging.Ordering.OrderCompleted` (integration event, Commit 4) chiếm. Thay vào đó **tái dùng `OrderConfirmed`** (đã có từ Phase 4) — "một order được saga dẫn hết luồng" đúng là ý nghĩa cũ của Confirmed, chỉ đổi ai được phép tạo ra event đó (xem mục kế).
- **Chốt số phận endpoint confirm/cancel thủ công Phase 4 → admin-override:** `Order.Confirm()` **giữ nguyên guard cũ** (chỉ chạy khi `Status == Pending`) — nhưng đường saga đi **không qua** guard này: saga hoàn tất khi `Status == TicketsIssued`, guard sẽ chặn nếu gọi `Confirm()`. `OrderCompletedConsumer` (mới) append thẳng `OrderConfirmed` qua `IOrderStore`, bỏ qua `Order.Confirm()` — đúng ý "saga là đường lệnh nội bộ đáng tin, khác đường lệnh thủ công của khách/admin". 2 endpoint `/confirm`/`/cancel` REST cũ **không đổi code**, chỉ đổi ý nghĩa: giờ là **admin-override** cho order còn Pending (ví dụ ép xác nhận không qua thanh toán) — ghi rõ trong doc comment.
- **4 consumer độc lập với saga** (`Ordering.Api/Consumers/`), mỗi cái append đúng 1 milestone — **tách khỏi `PurchaseSaga`, không nhét vào `.Then()` của state machine**: `OrderSeatsConfirmedConsumer` (nghe `SeatsConfirmed`), `OrderPaymentAuthorizedConsumer` (nghe `PaymentProcessed`), `OrderTicketsIssuedConsumer` (nghe `TicketsIssued`), `OrderCompletedConsumer` (nghe `Contracts.Messaging.Ordering.OrderCompleted`). RabbitMQ tự nhân bản mỗi event tới **cả saga lẫn consumer này** (2 hàng đợi riêng, cùng bind vào exchange của message type — pub/sub, không phải cạnh tranh) — xác nhận đúng bằng thực nghiệm (xem Verify). Lý do tách: saga lo **điều phối** (state tạm, gửi lệnh kế tiếp); consumer này lo **audit trail** (Order event stream) — đúng kiến trúc quyết định mục 3, và khớp cách hành văn của plan gốc ("qua IOrderStore/**command nội bộ ordering**").
- **🐛 Phát hiện thật: `MassTransit.Marten` KHÔNG có Outbox, plan giả định sai.** Kiểm tra lại đúng package đã reflect ở Commit 4 (`MassTransit.MartenIntegration.dll`) — chỉ có `MartenSagaRepository`, không có type nào liên quan Outbox (khác `MassTransit.EntityFrameworkCore` mà Catalog dùng — `AddEntityFrameworkOutbox`/`UseBusOutbox`). **`PlaceOrderCommandHandler` publish `OrderSubmitted` là best-effort, KHÔNG transactional** — crash giữa `SaveChangesAsync` (event `OrderPlaced` đã commit) và `PublishAsync` để lại order `Pending` mồ côi, saga không bao giờ khởi động. Ghi nhận backlog rõ ràng trong code comment: hướng sửa đúng-kiểu-Marten là **async daemon subscription** (durable, checkpoint, sống sót qua crash) relay `OrderPlaced` → `OrderSubmitted` — cùng loại trade-off đã defer cho `MyOrdersProjection` (Inline vs Async, Phase 4). Không chặn DoD Phase 5 (DoD nói về saga compensation, Commit 10, không phải cửa sổ crash này).
- **Port mới `IIntegrationEventPublisher`** (`Ordering.Application.Abstractions`, y hệt shape Catalog's) thay vì tiêm thẳng `MassTransit.IPublishEndpoint` vào `PlaceOrderCommandHandler` — lý do: giữ Application không phụ thuộc trực tiếp MassTransit cho ĐƯỜNG NÀY (khác Saga, vốn cần MassTransit thật để định nghĩa state machine), và test handler chỉ cần fake 1 method thay vì toàn bộ `IPublishEndpoint`. `MassTransitIntegrationEventPublisher` (Infrastructure) implement, đăng ký trong `AddOrderingInfrastructure`.
- `PlaceOrderCommand` mở rộng: `HoldId`, `PaymentMethod`, `CustomerName`, `CustomerEmail` (2 field cuối là request field trần, giống `CustomerId` từ trước — Ordering **chưa có** JWT/claims extraction ở đâu cả, đây không phải pattern mới, chỉ nối dài pattern cũ; auth thật là việc khác, chưa làm).
- **🐛 Bug thật phát hiện qua Docker health check (không phải qua unit test) — event cũ không deserialize được:** sau khi đổi `OrderPlaced.SeatIds` sang `Guid`, `MartenHealthCheck` (`/health`) trả `503 Unhealthy`: `EventDeserializationFailureException` trên **15 order test cũ từ Phase 4** (SeatIds dạng `["A1","B2"]`) — Marten cố parse string thành Guid, ném lỗi ngay tại event đầu tiên trong stream (`QueryAllRawEvents().Take(1)`). Đây là **bài học event-sourcing thật**: đổi shape của 1 event đã persist làm gãy replay của lịch sử cũ — sản phẩm thật cần Marten's event **upcasting** để dịch shape cũ→mới lúc đọc; ở đây **không làm** vì toàn bộ 15 order đó là dữ liệu test/dev từ chính session này, không có giá trị giữ lại. **Đã xin phép người dùng** (bị chặn bởi safety layer vì là `TRUNCATE ... CASCADE` không có predicate hẹp) → được duyệt → `TRUNCATE mt_events, mt_streams, mt_doc_myorderview, mt_doc_purchasestate, ... RESTART IDENTITY CASCADE` trên `ticketflow_ordering` → health lại `200 Healthy`.
- **Verify thật qua gateway, đầy đủ happy path (không harness, RabbitMQ + Postgres thật):**
  1. `POST /ordering/orders` qua gateway (JSON đủ field mới) → `201`, `status: Pending`; `psql` xác nhận `mt_doc_purchasestate` tạo với `AwaitingSeats` (saga khởi động thật từ HTTP call thật, không phải publish tay).
  2. Scratch app (đã xoá sau khi xong) publish lần lượt `SeatsConfirmed`/`PaymentProcessed`/`TicketsIssued` (RabbitMQ thật) cho đúng `OrderId` → sau mỗi bước, `psql mt_events` xác nhận đúng milestone được append (`order_seats_confirmed`/`order_payment_authorized`/`order_tickets_issued`/`order_confirmed`, đúng thứ tự `seq_id` 1→5) **VÀ** `GET /ordering/orders/{id}` qua gateway (đọc `MyOrderView` — không phải `Order` trực tiếp, đúng CQRS split Commit 6 sẽ nhấn mạnh) trả đúng `status` từng bước: `SeatsConfirmed` → `PaymentAuthorized` → cuối cùng `Confirmed` (kèm `confirmedAt` đã điền). Saga tự finalize đúng (`mt_doc_purchasestate` 0 rows sau bước cuối).
  3. Container log sau redeploy xác nhận đủ **5 endpoint mới lên hàng đợi đúng tên**: `ordering-order-seats-confirmed`, `ordering-order-payment-authorized`, `ordering-order-tickets-issued`, `ordering-order-completed`, cộng `ordering-purchase-state` (saga, từ Commit 4) — 4 consumer mới không đụng hàng đợi của saga (đúng lý thuyết pub/sub 1 exchange nhiều queue).
  4. Regression cuối: **14 container** Up; gateway smoke test 4/4 `200`; toàn bộ test suite: `Ordering.Domain.Tests` 9/9 (thêm 1 test milestone chain), `Ordering.Application.Tests` 11/11, `Catalog.Domain.Tests` 35/35, `Payment.Domain.Tests` 9/9, `Payment.Infrastructure.Tests` 3/3, `Ticketing.Domain.Tests` 4/4 (**71/71 tổng, 0 regression**).

---

## Nhóm C — Nối participant thật vào saga

### Commit 6 — `feat(reservation): ConfirmSeats/ReleaseSeats command consumers`
- Consumer `ConfirmSeats` → gọi `ReservationService.ConfirmAsync` (Phase 2, **không viết lại Lua**) → publish `SeatsConfirmed` (kèm seatIds) hoặc `SeatsConfirmationFailed` (hold hết hạn/không tồn tại). Consumer `ReleaseSeats` → `ReleaseAsync` → publish kết quả (dùng cho compensation).
- Reservation giờ ref `TicketFlow.Messaging` + đăng ký consumer (`serviceName: "reservation"`).
- **Verify thật:** hold ghế thật (REST) → gửi `ConfirmSeats` qua bus → `psql`/Redis xác nhận ghế `Sold`; hold hết TTL rồi mới confirm → `SeatsConfirmationFailed`.

### Commit 7 — `feat(payment): ProcessPayment/RefundPayment + resiliency + idempotency`
- Consumer `ProcessPayment` → gọi `FakePaymentGatewayClient` (Polly) → ghi `Payment` (Postgres) → publish `PaymentProcessed`(PaymentId) / `PaymentFailed`(reason). Consumer `RefundPayment` → hoàn tiền idempotent → `PaymentRefunded`.
- Bật **MassTransit Inbox** (payment DbContext) + idempotency key theo `OrderId` — xử lý `ProcessPayment` 2 lần chỉ charge 1 lần.
- **Verify thật:** gửi `ProcessPayment` bình thường → `PaymentProcessed` + row Postgres; gửi lại y hệt (trùng) → **không** charge lần 2; gửi với Amount ép-fail → Polly retry đúng số lần → cuối cùng `PaymentFailed`.

### Commit 8 — `feat(ticketing): IssueTickets + QR/PDF/MinIO + idempotency`
- Consumer `IssueTickets` → sinh QR/ghế → render PDF → upload MinIO → ghi `Ticket` (Postgres) → publish `TicketsIssued`(pdfUrls) / `TicketingFailed`. Bật Inbox + dedup theo `OrderId` (phát vé 2 lần = no-op, trả về vé đã phát).
- **Verify thật:** gửi `IssueTickets` → `TicketsIssued` + PDF trong MinIO + row Postgres; gửi lại trùng → không tạo PDF/vé mới; ép lỗi (MinIO down) → `TicketingFailed`.

---

## Nhóm D — Compensation + failure tests + chốt

### Commit 9 — `feat(ordering): saga compensation branches + notify`
- Nhánh bù trừ trong saga (đúng bảng compensation scope mục 7):
  - `PaymentFailed` → send `ReleaseSeats` → on release done → append `SeatsReleased`+`OrderCancelled`, publish `OrderCancelledIntegrationEvent`, Finalize.
  - `TicketingFailed` → send `RefundPayment` → on `PaymentRefunded` → send `ReleaseSeats` → append `Refunded`+`SeatsReleased`+`OrderCancelled`, Finalize.
  - `SeatsConfirmationFailed` (hold hết hạn) → append `OrderCancelled` ngay (chưa charge, chưa cần refund/release).
- Happy path cuối: send `NotifyOrderCompleted` → `notification` consume → log/gửi "vé của bạn" (tái dùng notification Phase 3).
- **Verify thật:** đi từng nhánh bằng participant thật + ép lỗi.

### Commit 10 — `test(saga): 3 kịch bản lỗi — DoD compensation` ⭐
- **Đây là bằng chứng DoD phần 2.** Verify runtime thật (Docker, không mock), 3 kịch bản scope mục 7:
  1. **Payment fail:** hold → place order (Amount ép-fail) → saga: ConfirmSeats OK → ProcessPayment fail → **ReleaseSeats** → ghế về `Available` (Redis+Postgres), Order `Cancelled`. Xác nhận **không charge**, ghế **được nhả**.
  2. **Ticketing fail:** hold → place (ép MinIO/ticketing lỗi) → saga: Confirm OK → Payment OK → IssueTickets fail → **RefundPayment** (Postgres payment `Refunded`) → **ReleaseSeats** → Order `Cancelled`. Xác nhận **tiền được hoàn**, ghế nhả.
  3. **Hold hết TTL trước thanh toán:** hold TTL ngắn → chờ hết hạn → place order → ConfirmSeats fail (hold không còn) → Order `Cancelled` ngay, không charge. Redis tự nhả (không cần code).
- Cân nhắc Testcontainers cho 1-2 kịch bản (như Phase 2 Commit 10) — quyết định lúc code (saga end-to-end cần nhiều container, có thể verify thủ công qua Docker là đủ).

### Commit 11 — `chore: finalize Phase 5 — routes, DoD checklist, reconcile ports`
- `.slnx` đủ project mới; `docker compose ps` đủ container (thêm payment, ticketing, minio); gateway route `payment`/`ticketing` (nếu phơi query endpoint xem trạng thái thanh toán/vé).
- **Reconcile scope port table** (đã hỏi & duyệt): payment 8007, ticketing 8008 (không gRPC), notification/search giữ 8005/8006. Tick DoD Phase 5.
- Happy path end-to-end 1 lần cuối qua gateway; cập nhật doc này.

---

## Sơ đồ PurchaseSaga (state machine)

```mermaid
stateDiagram-v2
    [*] --> AwaitingSeats : OrderSubmitted / send ConfirmSeats
    AwaitingSeats --> AwaitingPayment : SeatsConfirmed / send ProcessPayment
    AwaitingPayment --> AwaitingTickets : PaymentProcessed / send IssueTickets
    AwaitingTickets --> Completed : TicketsIssued / notify + OrderCompleted
    Completed --> [*]

    AwaitingSeats --> Cancelled : SeatsConfirmationFailed
    AwaitingPayment --> Compensating_Seats : PaymentFailed / send ReleaseSeats
    AwaitingTickets --> Compensating_Refund : TicketingFailed / send RefundPayment
    Compensating_Refund --> Compensating_Seats : PaymentRefunded / send ReleaseSeats
    Compensating_Seats --> Cancelled : SeatsReleased
    Cancelled --> [*]
```

*Saga (coordinator) chạy state machine này; mỗi transition thành công cũng append domain event tương ứng vào **event stream của Order** (Phase 4) — nên Order phản ánh đúng lifecycle, `MyOrderView` cập nhật, và stream tồn tại vĩnh viễn kể cả sau khi saga instance bị finalize.*

---

## Kiểm chứng DoD cuối Phase 5

| DoD | Kiểm chứng |
|---|---|
| Mua vé end-to-end thành công | Commit 9/11: hold → place → saga Confirm→Payment→Tickets→Notify → Order `Completed`, PDF trong MinIO, `MyOrderView` = Completed |
| Payment fail → tự bù trừ | Commit 10 KB1: ProcessPayment fail → ReleaseSeats → ghế `Available`, Order `Cancelled`, không charge |
| Ticketing fail → hoàn tiền + nhả ghế | Commit 10 KB2: IssueTickets fail → RefundPayment → ReleaseSeats → Order `Cancelled` |
| Hold hết hạn → ghế trở lại còn trống | Commit 10 KB3: Redis TTL tự nhả → ConfirmSeats fail → Order `Cancelled` (không code mới) |
| Idempotent | Commit 7/8: gửi trùng ProcessPayment/IssueTickets → không charge/phát vé lần 2 (Inbox + key nghiệp vụ) |
| Resiliency | Commit 2/7: Polly retry + circuit breaker quanh fake payment gateway |

## Hướng mở rộng (backlog — không thuộc Phase 5)
- **Scan vé tại cổng** (`gate-scanner` validate QR): Phase 8 (frontend + scanner).
- **Cổng thanh toán thật** (Stripe/VNPay...): thay `FakePaymentGatewayClient`; interface đã tách sẵn để đổi adapter.
- **Saga timeout/scheduling** (MassTransit `Schedule`) cho bước treo lâu (payment không phản hồi) — thêm khi cần siết SLA.
- **Bulkhead isolation** (Ch10) — nếu 1 participant chậm làm nghẽn pool: thêm Polly bulkhead, chưa cần ở quy mô học.

## Thứ tự đọc sách trước khi code (scope mục 13)
Đọc **Ch9 (Saga)** — orchestration vs choreography, distributed transaction không 2PC, compensation/rollback ngược, state machine. Và **Ch10 (Resiliency)** — retry, circuit breaker, timeout, bulkhead, idempotency, graceful degradation. Bổ sung ngoài sách: **MassTransit State Machine Saga** (Automatonymous API: `Initially`/`During`/`When`/`TransitionTo`), **saga persistence** (Marten saga repo), **MassTransit Inbox/Outbox** cho idempotency; **Polly v8** (`ResiliencePipeline`, khác v7 `Policy`); tài liệu **QuestPDF** (Community license), **QRCoder**, **MinIO .NET SDK**.
