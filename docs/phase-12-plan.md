---
title: "Phase 12 — Reliability, consistency & security hardening — Kế hoạch commit"
phase: 12
chapters: [Ch4 Messaging, Ch8 Data consistency, Ch9 Saga, Ch10 Resiliency, Ch13 Security, Ch14 Production hosting, Ch17 Wrap-up]
created: 2026-07-19
tags: [ticketflow, phase-12, plan, hardening, dual-write, concurrency, idempotency, saga, security, disaster-recovery, e2e]
---

# Phase 12 — Kế hoạch chi tiết theo commit

> **Mục tiêu:** khắc phục các production blocker tìm thấy trong đợt review toàn hệ ngày 2026-07-19: checkout tin dữ liệu client, hold/order không ràng buộc cùng tập ghế, dual-write Redis/Postgres/broker/object-store/payment-provider, message đến trùng hoặc sai thứ tự, saga không có timeout/recovery, authorization không theo resource, và hạ tầng correctness-critical không bền vững.
>
> **DoD:** không thể đổi ghế/giá/tiền tệ từ client; cùng một business operation chỉ charge/confirm/issue/refund một lần dù message trùng hoặc process chết; compensation không thể đụng allocation của đơn khác; saga tự hồi phục hoặc đi tới trạng thái cần can thiệp có quan sát được; ticket và organizer resource được kiểm tra ownership; Redis/RabbitMQ/Kafka/Keycloak restart không làm mất correctness; CI chạy trên `main` và có test failure-boundary/concurrency/E2E thật.

> ⚠️ **Viết TRƯỚC khi code.** Bảy sự thật phải giữ xuyên suốt Phase 12:
> 1. **Đây là remediation phase, không phải feature phase.** Không thêm tính năng sản phẩm mới trước khi các invariant tiền/ghế/ownership ở Nhóm A-C khép.
> 2. **Không có “exactly once delivery” xuyên HTTP + Postgres + Redis + RabbitMQ + Kafka + payment provider.** Mục tiêu đúng là *effectively-once business outcome*: stable operation key, local transaction, inbox/outbox, conditional state transition và reconciliation.
> 3. **Redis không còn là source of truth cho ghế đã bán.** Redis chỉ tăng tốc hold/availability; allocation bền vững có owner + generation trong Postgres mới quyết định confirm/release/rebuild.
> 4. **Retry một external write không có idempotency key là bug.** Charge, refund, upload PDF, gửi email và publish sang transport khác đều phải có operation key + trạng thái `Pending/Unknown/Completed` + reconcile.
> 5. **Schema/message contract sẽ đổi theo kiểu expand → migrate → contract, không big-bang.** Consumer đọc được cả v1/v2 phải deploy trước producer v2; chỉ xóa field/behavior cũ sau khi backfill và kiểm chứng không còn message cũ.
> 6. **81 test .NET + frontend lint/build đang xanh nhưng chưa chứng minh các boundary này.** Không đánh dấu commit `DONE` nếu chỉ unit test xanh; mỗi commit dưới đây có failure/concurrency gate riêng.
> 7. **Plan là giả thuyết có thể bị phản biện, không phải mệnh lệnh thi công máy móc.** Người thực hiện có quyền và trách nhiệm chỉ ra giả định sai, thử probe và chọn cách làm khác khi code thật, tài liệu hoặc bằng chứng cho thấy phương án an toàn/tốt hơn. Khi lệch plan phải ghi lại bằng chứng, trade-off, invariant/DoD bị ảnh hưởng và cập nhật plan/ADR ngay trong commit liên quan; quyền phản biện không được dùng để hạ tiêu chuẩn correctness, bỏ gate kiểm chứng hoặc tự ý mở rộng scope.

---

## Bối cảnh: cái gì đang đúng, cái gì đang nguy hiểm

- **Đang có và tái sử dụng:**
  - Catalog đã có EF transactional outbox; Reservation có Lua atomic cho thao tác bên trong Redis; Ordering dùng Marten event store; participant giao tiếp qua MassTransit/RabbitMQ; Analytics dùng Kafka; frontend đã có BFF/Auth.js/SignalR.
  - 81 test .NET hiện tại xanh; `pnpm -r run lint` và `pnpm -r run build` xanh. Đây là regression baseline phải giữ.
  - Phase 10 đã có trace/metrics/log; Phase 11 đã có kind/Helm/load test. Phase 12 dùng chính observability và môi trường đó để chứng minh recovery, không dựng một stack kiểm thử song song mới.
- **Gap correctness cần đóng:**
  - `PlaceOrder` nhận seat IDs, amount và currency từ browser; Reservation confirm theo `HoldId`, còn saga ticket/compensate theo seat IDs client gửi.
  - Reservation confirm/unsell/inventory là nhiều write nối tiếp giữa Redis và Postgres, không có durable operation receipt hay owner để retry an toàn.
  - Payment kiểm tra DB trước khi gọi provider; hai message khác `MessageId` nhưng cùng `OrderId` có thể charge hai lần. Refund hiện chỉ đổi local status.
  - Order stream có nhiều writer độc lập, không expected version; milestone đến sai thứ tự có thể làm aggregate regress. Saga không timeout và đặt Kafka analytics trên critical path.
  - Ticket PDF upload trước DB; check-in là read-then-write; ticket API không kiểm tra customer owner/event/gate.
  - Catalog lifecycle event không có revision; Search/Notification có thể resurrect event đã cancel. Organizer onboarding và Identity registration là distributed workflow không bền vững.
  - Redis/RabbitMQ/Kafka/Keycloak trong chart hiện không có persistent storage; workflow CI theo dõi `master` trong khi nhánh thật là `main`.

---

## Quyết định kiến trúc cho Phase 12

### 1. Checkout contract v2: client gửi **ý định mua**, server quyết định ghế + giá
- Request v2 chỉ chứa `holdId`, `paymentMethod`, `idempotencyKey` và thông tin liên hệ không ảnh hưởng giá. `customerId` luôn lấy từ JWT; không nhận `seatIds`, `totalAmount`, `currency`, `eventId` như dữ liệu có thẩm quyền.
- Reservation trả `ConfirmedAllocation`/`HoldQuote` bền vững gồm `EventId`, exact `SeatIds`, `PriceSnapshotVersion`, `TotalAmount`, `Currency`, `OwnerUserId`, `ExpiresAt`. Ordering persist đúng snapshot này trước khi saga chạy.
- Catalog là owner của sale policy và price tier. Catalog phát snapshot có revision sang Reservation; hold mới chỉ tạo khi event `Published`, nằm trong sale window và snapshot hiện hành. Hold đã tạo được giữ giá tới `ExpiresAt`; cancel event vô hiệu ngay mọi hold chưa confirm.
- `Idempotency-Key` là business key bắt buộc. Cùng key + cùng payload trả lại kết quả cũ; cùng key + payload khác trả `409`, không tạo order thứ hai.

### 2. Postgres `SeatAllocation` là source of truth; Redis là acceleration layer có thể rebuild
- Bổ sung durable model tối thiểu: `SeatId`, `EventId`, `HoldId`, `OrderId?`, `OwnerUserId`, `State`, `Generation`, `ExpiresAt`, `PriceSnapshotVersion`, `Amount`, `Currency`, concurrency token.
- Bổ sung `ReservationOperation`/receipt unique theo `(OperationType, BusinessKey)` để redelivery trả lại **cùng outcome**, không suy diễn “hold không còn = lần trước thất bại”.
- Redis lưu owner + generation, không chỉ set seat ID. Mọi confirm/release Lua script là compare-and-set theo expected owner/generation. Reconciler có thể dựng lại available/held/sold view từ Postgres và phát hiện drift hai chiều.
- SignalR chỉ nhận event sau commit/outbox; notifier chết không được làm API/consumer báo mutation thất bại.

### 3. Một local transaction cho state + outbox; inbox chặn delivery trùng, business key chặn semantic duplicate
- Mỗi consumer ghi inbox, domain state và outbox trong **cùng datastore transaction** của service. Acknowledge message chỉ sau transaction đó commit.
- `MessageId` chỉ chặn redelivery cùng envelope. Mọi thao tác quan trọng còn có business key riêng: `OrderId` cho payment, `(OrderId, HoldId)` cho confirm/release, `OrderId` cho issue tickets, `(TicketId, GateOperationId)` cho check-in.
- Nếu MassTransit/Marten integration hiện tại không thể đảm bảo cùng transaction cho saga state + outbox, viết local Ordering outbox/dispatcher trong Postgres/Marten; **không chấp nhận in-memory outbox** như giải pháp durability.

### 4. Purchase process manager là writer duy nhất của Order lifecycle
- Loại mô hình nhiều audit consumer cùng append stream độc lập. Participant response đi vào một endpoint partition theo `OrderId`; process manager quyết định transition và append với expected stream version.
- Late/duplicate message được ghi nhận nhưng không làm state regress. Terminal state (`Confirmed`, `Cancelled`, `RefundFailed`, `ManualReview`) là monotonic.
- Admin confirm/cancel cũng đi qua process manager, không append trực tiếp vào stream và không chạy song song vô thức với saga.

### 5. Payment là durable state machine, không phải `try/catch` quanh `ChargeAsync`
- `PaymentAttempt` unique theo `OrderId`, trạng thái `Pending → Authorized | Declined | Unknown`; provider idempotency key = `OrderId`/stable attempt ID.
- Timeout/mất response sau khi provider có thể đã charge đi vào `Unknown`, không phát `PaymentFailed`, không release seat cho tới khi reconcile bằng provider lookup.
- Refund có API/provider reference thật và state `RefundPending → Refunded | RefundDeclined | RefundUnknown`; chỉ phát `PaymentRefunded` sau xác nhận hoặc reconcile.

### 6. Canonical business completion trước, fan-out analytics/notification sau
- Một `OrderCompleted` canonical fact được persist trong Ordering. RabbitMQ notification và Kafka `SaleRecorded` là hai relay độc lập có checkpoint/idempotency riêng.
- `CompletedAt` được tạo một lần và giữ ổn định; ingestion/revision timestamp tách riêng để ClickHouse dedup không làm sale chạy sang reporting window khác.
- Kafka, email hoặc Elasticsearch outage không được giữ một đơn đã paid + ticketed ở trạng thái chưa hoàn tất.

### 7. Lifecycle event có revision; projection chỉ tiến tới, không lùi
- Mọi Catalog event mang `AggregateVersion`, `OccurredAt`, `EventId`; cancellation tạo tombstone có revision thay vì dựa vào thứ tự arrival.
- Mongo/Elasticsearch chỉ apply revision mới hơn; old event mặc định revision `0` để phục vụ rolling migration nhưng không được overwrite document v2.
- Mọi response Elasticsearch phải kiểm tra `IsValidResponse`; transient failure throw để retry/DLQ hoạt động, not-found thật phải khác backend outage.

### 8. Authorization theo resource ở service sở hữu dữ liệu
- Gateway vẫn là lớp đầu, nhưng Catalog/Reservation/Ordering/Ticketing/Analytics tự validate JWT và resource ownership. Direct service port/network route không được bypass policy.
- Persist `OrganizerId/TenantId`, `CustomerId`, `EventId`, gate assignment và ticket revocation state. Cross-organizer/customer trả `404` hoặc `403` theo policy nhất quán.
- Internal call dùng client credential/audience riêng; production bổ sung network policy/mTLS theo khả năng môi trường, không dùng “tin Docker network” như security boundary.

### 9. Distributed onboarding/registration trở thành workflow có resume/reconcile
- Event onboarding có stable creation key và step state cho Venue → Event → SeatMap → Price → Inventory → Publish; retry resume bước dang dở, không tạo resource mới.
- Identity registration lưu `Pending/Completed/CompensationPending`, tìm lại Keycloak user theo stable normalized identity khi create outcome không chắc chắn, cleanup bằng token độc lập có timeout.
- Notification delivery và ticket artifact cũng theo cùng pattern operation record + provider/object key idempotent.

### 10. Rollout theo **expand → shadow/reconcile → cutover → contract**
- Thêm cột/table/index nullable và consumer hiểu v2 trước; backfill ownership/revision; chạy reconciler ở report-only; mới bật writer v2 bằng feature flag.
- Trong thời gian chuyển đổi, v1 checkout chỉ bật ở local/test và log cảnh báo; production cutover phải từ chối request còn gửi amount/seat IDs như authority.
- Không drop key/schema v1 cho tới khi queue cũ rỗng, dữ liệu reconcile bằng 0 và rollback window kết thúc.

---

## Nhóm A — Khóa invariant bằng test + schema/contract nền

### Commit 1 — `test(hardening): failure-boundary harness + regression cases cho tiền/ghế/message order` ⭐ ✅ DONE
- Thêm test fixture dùng Postgres + Redis + RabbitMQ/Marten thật; payment/object/email dùng counting fake có điều khiển barrier, timeout và “side effect thành công nhưng mất response”.
- Viết test đang **đỏ có chủ đích** cho các exploit/race đã tìm thấy:
  - hold seat A nhưng order/ticket seat B; payment failure không được release B;
  - amount `0`, currency giả, event Draft/Cancelled;
  - hai `ProcessPayment` khác `MessageId` cùng `OrderId` chỉ được gọi provider một lần;
  - crash sau từng boundary Redis → Postgres → outbox/publish;
  - stale `ReleaseSeats` của order A sau khi seat đã thuộc order B;
  - mọi permutation/duplicate của `SeatsConfirmed`, `PaymentProcessed`, `TicketsIssued`, `OrderCompleted`;
  - hai scan đồng thời chỉ một `Valid`;
  - Published/Rescheduled/Cancelled tới sai thứ tự không resurrect event.
- Test failure-boundary phải có tên theo invariant và comment đúng failpoint; tránh test bám chi tiết implementation khiến refactor vô nghĩa.
- **Gate:** lưu kết quả baseline đỏ/expected-failure trong doc/test category riêng; regression suite cũ vẫn 81/81 xanh. Từ Commit 3 trở đi từng test được chuyển sang pass, không `Skip` vĩnh viễn.

**Kết quả thực tế (2026-07-20):** thêm project explicit gate `tests/TicketFlow.Phase12.HardeningTests` (cố ý chưa đưa vào `TicketFlow.slnx`), không có test `Skip`. Trait `Phase12Harness` xanh **5/5** với Postgres 16 + Marten, Redis 7 và RabbitMQ 3 thật; trait `Phase12ExpectedFailure` chạy **27 case: 23 đỏ đúng blocker, 4 xanh là control case của các permutation hợp lệ**. Bảy test project regression cũ vẫn **81/81 xanh**. Counting fake dùng chung đã chứng minh barrier, timeout và trạng thái "side effect thành công nhưng mất response" cho payment/object/email. Danh sách case, lệnh chạy và promotion rule nằm tại [`docs/phase-12-hardening-baseline.md`](phase-12-hardening-baseline.md).

### Commit 2 — `feat(contracts,data): expand schema v2 — operation keys, ownership, revisions, currency` ✅ DONE
- Thêm message/request v2 và schema nullable/index trước, chưa cutover behavior:
  - checkout `IdempotencyKey`, hold quote/allocation result;
  - `Currency`, `PriceSnapshotVersion`, `AggregateVersion`, stable `CompletedAt` xuyên contracts;
  - Reservation allocation/operation receipt + owner/generation;
  - Payment attempt/refund state + gateway reference;
  - Ticket `CustomerId`, `EventId`, issuance state, revocation; Catalog `OrganizerId` + concurrency token;
  - inbox/outbox unique indexes và Recommendation unique `UserId`.
- Consumer đọc được v1/v2; unknown/missing revision = `0`; log metric đếm traffic/message v1 để biết khi nào contract được.
- Migration dùng expand-only, không backfill phỏng đoán owner nếu nguồn không đủ. Record không xác định owner phải vào report/manual-review, không tự gán.
- **Verify:** migration chạy từ database phase 11 và database trắng; rollback binary cũ vẫn đọc được schema mới; contract serialization compatibility test xanh.

**Kết quả thực tế (2026-07-20):** thêm `CheckoutIntentV2`, `HoldQuoteV2`, `ConfirmedAllocationV2` và các field optional cuối record cho contract đang dùng, nên payload v1 deserialize với revision/snapshot `0` và không làm caller cũ đổi chữ ký. `ticketflow.contract.v1_traffic_total` được ghi tại các consumer Ordering/Payment/Reservation/Ticketing/Search và các service đó đã đăng ký meter để Prometheus scrape.

Schema chỉ expand: Reservation có `seat_allocations` (owner nullable, generation, quote/price, `xmin` concurrency) và receipt unique `(OperationType, BusinessKey)`; Payment có `payment_attempts`/`payment_refunds`; Ticket có field ownership/issuance/revocation nullable; Catalog có `OrganizerId` nullable + `xmin`; Mongo tạo unique index `purchaseHistory.UserId` lúc startup. Không có writer v2, backfill hay gán owner tự động. Nếu index Mongo gặp dữ liệu trùng, startup fail rõ để manual review thay vì chọn/gộp một record ngầm.

**Điều chỉnh plan có bằng chứng:** Catalog/Payment/Ticketing đã có unique inbox/outbox trong migration trước nên không tạo lại index trùng; Reservation chưa có EF inbox/outbox transaction nên cố ý chưa thêm một index không được consumer dùng — phần này vẫn thuộc Commit 4. Phiên bản Npgsql hiện tại không có `UseXminAsConcurrencyToken`; dùng shadow `xmin`/`IsRowVersion` thay thế. EF cố sinh cột vật lý `xmin`, nhưng `xmin` là system column của PostgreSQL nên migration đã được hiệu chỉnh để không `ADD COLUMN`, tránh migration lỗi trên DB thật.

**Gate đã chạy:** hardening harness xanh **10/10**, gồm JSON v1/v2 serialization, database trắng migrate lên head, database ở migration cuối trước Phase 12 upgrade lên head và query bằng tập cột v1; `TicketFlow.slnx` regression xanh **81/81**. Migrations được sinh qua design-time factory riêng, do đó EF tooling không khởi động Redis/Lambda/MinIO/Elasticsearch/RabbitMQ.

---

## Nhóm B — Khép tiền + ghế trước khi sửa phần còn lại

### Commit 3 — `feat(checkout): server-authoritative hold quote, sale policy, price & currency` ⭐ ✅ DONE
- Catalog publish sale-policy/price snapshot có revision; Reservation consume idempotent và dùng snapshot để tạo immutable hold quote.
- API Ordering v2 chỉ nhận `HoldId + PaymentMethod + IdempotencyKey`; identity lấy từ claims; handler load/validate quote owner, event, expiry và exact seats từ server.
- Bỏ client amount/currency/seat IDs khỏi authority path. Trong compatibility window có thể parse field cũ nhưng **không sử dụng**; metric/log phát hiện client cũ.
- Analytics schema/query tách currency; nếu business chốt chỉ VND thì validate cứng ở Catalog/Reservation thay vì silently default.
- Event cancel đóng sale ngay; public Catalog chỉ trả event publish/saleable theo endpoint semantics.
- **Verify:** test tamper amount/currency/seat/event/owner đều fail trước side effect; mixed price tier tính đúng; stale price revision có policy rõ; Draft/Cancelled/past event không hold/order được.

**Kết quả thực tế (2026-07-20):** Catalog phát `CatalogSalePolicyChanged` versioned qua transactional outbox khi Publish/Reschedule/Cancel, gồm toàn bộ section price snapshot và trạng thái/tombstone. Reservation consume theo `PriceSnapshotVersion` (snapshot cũ/duplicate không được lùi state), chỉ tạo hold cho event `Published`, trong sale window, ghế thật thuộc đúng event và có price tier; amount/currency được tính hoàn toàn phía server. Hold quote immutable được persist với exact seats, owner từ JWT `sub`, snapshot version và expiry; cancel event vô hiệu quote ngay, còn snapshot giá mới không đổi giá hold đã tạo trước đó.

Ordering production `POST /orders` chỉ nhận `HoldId + PaymentMethod + IdempotencyKey`, lấy owner-scoped quote qua gRPC và phát `OrderSubmitted` bằng event/seats/amount/currency từ quote. Legacy request chuyển sang `/orders/legacy`, chỉ map trong `Development` và đếm metric v1. Saga giữ và truyền currency/snapshot/operation key qua confirm, payment, ticketing, refund/release và analytics. ClickHouse lưu `currency` + `price_snapshot_version`; revenue group/filter theo currency và không cộng các currency khác nhau thành một total giả.

**Điều chỉnh plan có chủ đích:** dùng bảng `hold_quotes` riêng thay vì bắt đầu ghi `seat_allocations`, để Commit 3 khóa quote authority nhưng không kích hoạt sớm Postgres allocation/source-of-truth trước transaction/reconcile của Commit 4. Domain hiện chưa có sale-window riêng; `Event.Schedule.StartsAt` là mốc đóng sale, còn thời điểm publish là mốc mở. Đây là reuse semantics hiện hữu, không tự thêm product policy mới.

**Gate đã chạy:** hardening harness xanh **22/22** trên Postgres/Redis/RabbitMQ thật, gồm migration database trắng/upgrade lên head, raw JSON tamper seat/amount/currency, owner scope, mixed tier, Draft/Cancelled/past event và stale-price hold. Category expected-failure còn **18 đỏ / 3 control xanh** cho các blocker thuộc Commit 4+; regression `TicketFlow.slnx` xanh **81/81**. Hai migration `Phase12AuthoritativeCheckout` chỉ add version columns và các bảng/index sale-policy/hold-quote.

### Commit 4 — `feat(reservation): durable SeatAllocation + idempotent confirm receipt + Redis rebuild` ⭐ ✅ DONE
- Persist allocation/receipt trước hoặc cùng logical operation; confirm redelivery trả lại exact confirmed seat IDs của lần đầu.
- Lua confirm kiểm tra `(HoldId, OwnerUserId, Generation, exact seat set)`; không xóa reverse index cuối cùng trước khi durable receipt có thể phục hồi outcome.
- EF inbox + outbox cùng transaction với durable seat state; SignalR publish từ outbox/non-critical notifier.
- Viết reconciler ba mode: `report`, `repair-cache`, `quarantine-conflict`. Startup không tự sửa conflict ownership mơ hồ.
- Redis bootstrap dựng lại inventory **gồm sold/held còn hạn**, không chỉ danh sách seat IDs; active hold hết hạn có rule rõ.
- **[Delta review 2026-07-21]** Khớp với deviation `hold_quotes` của Commit 3: định nghĩa rõ lifecycle **quote → allocation** ngay trong commit này — `ConfirmSeats` (mang operation key) load quote theo owner, tạo `seat_allocations` + receipt trong **cùng transaction**, quote đi tới terminal state (`Consumed`/`Expired`/`Invalidated`) thay vì nằm lại vĩnh viễn (Commit 3 chưa có bất kỳ cleanup nào cho `hold_quotes`), và unique mapping hold/quote → order đặt ở đây làm nền cho "unique hold-to-order" của Commit 7. Không để hai bảng quote/allocation cùng mô tả một ghế mà không có ràng buộc chuyển trạng thái giữa chúng.
- **[Delta review 2026-07-21]** **Xóa public HTTP `POST /holds/{holdId}/confirm`** (`HoldEndpoints`): sau Commit 3 endpoint này có `RequireAuthenticated` nhưng **không owner-scope và không gắn với order/payment nào** — bất kỳ user đăng nhập nào biết holdId đều chuyển được ghế sang Sold với 0 đồng; kể cả chính chủ hold cũng không được phép confirm ngoài saga. Đường confirm hợp lệ duy nhất là ConfirmSeats consumer (+ gRPC nội bộ). `POST /holds/{holdId}/release` giữ cho UX bỏ chọn ghế nhưng phải owner-scope theo quote. Bổ sung expected-failure test gọi tên exploit này — suite Commit 1 hiện chỉ phủ tamper mức message/request body, chưa phủ endpoint exposure.
- **Verify:** kill process tại mọi failpoint Redis/DB/outbox/SignalR rồi redeliver cho cùng outcome; xóa toàn Redis rồi rebuild không oversell, availability khớp Postgres; gọi HTTP confirm trực tiếp không còn route nào chấp nhận; release hold của user khác bị chặn.

**Kết quả thực tế — viết bổ sung ngày 2026-07-23, không phải lúc commit.** Commit `45d4878` đã ship đầy đủ nhưng mục này chưa từng được cập nhật: không có `✅ DONE`, không có đoạn kết quả, không có gate — dù Commit 5 và 7 đều viện dẫn công việc của nó như đã xong. Phần dưới đây dựng lại **từ code thật**, và vì thế không có số gate của thời điểm đó; số hiện hành nằm ở mục audit cuối Nhóm B.

Postgres là source of truth: `seat_allocations` (owner/generation/state/quote reference) cộng `ReservationOperationReceipt` unique theo `(OperationType, BusinessKey)` — replay `ConfirmSeats` trả lại đúng seat set của lần đầu thay vì suy diễn từ việc hold còn hay mất. Lifecycle quote → allocation khép đúng như delta 2026-07-21: `ConfirmAsync` load quote theo owner, tạo allocation + receipt trong cùng transaction, quote đi tới `Consumed`; `ReleaseQuoteAsync` đưa quote tới `Expired`/`Invalidated` chứ không để nằm lại. `ConfirmHoldScript` là CAS trên `(HoldId, OwnerUserId, Generation, exact seat set)` và claim `confirmedorder:{orderId}` trước khi chạm ghế, nên hai hold không thể cùng nhận một order.

Inbox/outbox: `ReservationDbContext` đăng ký `AddInboxStateEntity`/`AddOutboxMessageEntity`/`AddOutboxStateEntity`, `Program.cs` bật `AddEntityFrameworkOutbox` + `UseBusOutbox` và `UseEntityFrameworkOutbox` trên từng receive endpoint — nên `context.Publish` trong consumer chỉ rời hàng đợi sau khi transaction của seat state commit. SignalR cố ý **nằm ngoài** outbox, trên queue riêng, đúng nghĩa "notifier không critical".

Reconciler có đủ ba mode: `Report` read-only tuyệt đối (không expire quote, không dọn cache), `RepairCache` fail-closed cho cả event khi có ownership conflict thay vì tự chọn chủ, `QuarantineConflict` chứa ghế mâu thuẫn vào set riêng và vẫn rebuild phần an toàn. `SeatCacheBootstrapper` + `ReservationCacheReconciliationWorker` chạy định kỳ; rebuild dựng lại **sold + held còn hạn + reverse index**, không chỉ danh sách seat id.

Hai delta của commit này đã khép: `POST /holds/{holdId}/confirm` **không còn tồn tại** trong `HoldEndpoints` (chỉ còn create/quote/release), `POST /holds/{holdId}/release` owner-scope theo `sub` qua `ReleaseOwnedHoldAsync`, và `HoldEndpointSecurityTests` gọi tên đúng exploit đó.

### Commit 5 — `fix(reservation): owner-scoped compensation + idempotent inventory provisioning` ✅ DONE
- `ReleaseSeats`/`Unsell` bắt buộc expected `OrderId/HoldId/Generation`; conditional Lua + optimistic DB update chỉ release allocation còn thuộc operation đó.
- Stale/duplicate compensation trở thành no-op có metric, không chuyển seat của owner mới về Available.
- `EnsureInventory` idempotent: nếu Postgres đã có nhưng Redis thiếu thì reload/reconcile; concurrent provisioning dùng unique constraint và cùng result, không check-then-insert.
- Validate organizer/event/section/capacity trước allocate list; đặt hard limit request/seat count.
- **[Delta review 2026-07-20]** `UnsellAsync` hiện không broadcast SignalR (`ReservationService.cs`: `ReleaseAsync` notify "Available" nhưng `UnsellAsync` thì không) — ghế được compensation trả lại không bao giờ hiện "Available" trên seat map live cho tới khi client tự refetch. Khi SignalR chuyển sang publish-from-outbox (Commit 4), thêm notify cho đường unsell/compensation cùng lúc, cùng cơ chế.
- **[Delta review 2026-07-21]** Commit 3 đã khóa "mua **cái gì**, giá **bao nhiêu**" nhưng chưa giới hạn "**giữ bao nhiêu, bao lâu một lần**": thêm per-user cap (số hold active + tổng ghế đang giữ trên mỗi event) và rate limit tạo hold, trả 409/429 có body phân biệt được với sold-out thật. Không có cap, một user thường có thể loop hold toàn bộ venue mỗi TTL window mà không mua — griefing rẻ tiền mà flash-sale load test (đo oversell, không đo lock-out) không bắt được. Hard limit provisioning ở bullet trên chỉ chặn phía organizer, không chặn phía customer.
- **Verify:** delayed release sau resale, DB failure sau Redis mutation, concurrent provision và Redis-missing retry đều xanh; invariant `mỗi seat tối đa một active owner` được query/assert trực tiếp; seat map live thấy "Available" sau compensation; vượt hold cap/rate limit bị chặn có test riêng và không thể làm sold-out giả toàn venue.

**Kết quả thực tế (2026-07-22):** `ReleaseSeatsConsumer` không còn gọi `ReleaseAsync + UnsellAsync` (cả hai chỉ nhận event/hold/seat id nên không phân biệt được "ghế của tôi" với "ghế đã bán lại cho người khác"); thay bằng một `ReservationService.CompensateAsync` owner-scoped có receipt bền vững `ReleaseSeats` (unique theo `OperationKey`/`(orderId, holdId)`), cùng thứ tự khoá order → hold như `ConfirmAsync`. Quote bền vững là authority: message chỉ mang *kỳ vọng*, lệch event/owner/generation/seat set → `AuthorityMismatch`, không release gì. `SeatAllocation.ReleaseOwned` là CAS cấp dòng cho `(holdId, orderId, ownerUserId, generation)` và trả `OwnershipChanged` (no-op có metric) thay vì tự do hoá ghế; script Lua mới `ReleaseAllocationScript` là CAS cấp cache trên token `S|hold|order|owner|generation`, giữ nguyên marker khi còn ghế stale để reconciler còn bằng chứng.

`InventoryService.ProvisionAsync` trở thành idempotent thật: rows Postgres là authority, cache được nạp lại ở **mọi** lần gọi, nên retry sau bất kỳ failpoint nào cũng hội tụ thay vì chết cứng ở "already provisioned" — đúng dead-end đã ăn thật lúc verify Phase 10. Provisioning song song đi qua unique index `(EventId, Label)` sẵn có (bắt SQLSTATE 23505) rồi trả cùng seat count, không còn check-then-insert. Thêm validate organizer-side (section trùng id/tên, capacity, hard limit số section/ghế) và per-user cap + rate limit phía customer (`MaxActiveHoldsPerUserPerEvent`, `MaxHeldSeatsPerUserPerEvent`, `MaxHoldsPerUserPerWindow`) đếm từ `hold_quotes` bền vững — release ngay không refill budget, nên vòng lặp hold/release không lách được. Endpoint trả 429 cho rate limit và 409 cho cap, tách khỏi 409 sold-out thật.

**Delta đã khép trong commit này:** `UnsellAsync` không broadcast SignalR (delta 2026-07-20) — đường compensation giờ publish `"Available"` qua đúng outbox publisher của hold/confirm; per-user hold cap/rate limit (delta 2026-07-21).

**Điều chỉnh plan có bằng chứng:** (1) `ReleaseSeats` thêm `OwnerUserId` optional ở cuối record (expand-only, mục 10) — nhưng owner *authority* lấy từ quote bền vững, field trong message chỉ là cross-check; message v1 thiếu owner vẫn compensate được đúng thay vì bị chặn. (2) Consumer **vẫn publish `SeatsReleased`** kể cả khi không release được gì (stale/mismatch): giữ lại event sẽ treo saga vĩnh viễn ở `CompensatingSeats` thay vì tới `Cancelled`, trong khi ghế vốn đã không thuộc order này. Trường hợp đó không im lặng — có counter `ticketflow.reservation.compensation_total{outcome}` + log warning; chuyển sang `ManualReview` tường minh thuộc Commit 9. (3) Khi Redis và Postgres **bất đồng** về chủ sở hữu (cache báo stale nhưng DB vẫn gán cho order này), code throw để rollback cả receipt và retry trên view nhất quán — không tự chọn bên thắng, đúng quy tắc rollback của plan.

**Hồi quy phát hiện và sửa kèm (không thuộc scope Commit 5):** Commit 4 thêm `AllocationGeneration` vào `AuthoritativeHoldQuote` và đưa `AllocationGeneration <= 0` vào validate của `PlaceOrderV2CommandHandler`, nhưng helper `Quote()` trong `CheckoutAuthorityTests` vẫn dựng bằng 8 tham số positional → để mặc định `0` → **4 test trait Harness đỏ ngay từ HEAD trước khi Commit 5 bắt đầu**. Xác minh bằng `git stash` rồi chạy lại đúng 4 test đó trên HEAD sạch (4 failed / 2 passed) trước khi sửa, thay vì đoán.

**Gate đã chạy:** hardening trait `Phase12Harness` xanh **51/51** trên Postgres/Redis/RabbitMQ thật (gồm 5 case mới: stale-ABA không đụng ghế order B, compensation owner-scoped + replay receipt + publish "Available", message khai seat set của order khác bị chặn, retry provisioning sau failpoint Redis không nhân đôi inventory, re-provision khác shape bị từ chối, hold cap và rate-limit loop). Trait `Phase12ExpectedFailure` còn **14 đỏ / 3 control xanh** (trước Commit 5: 16 đỏ) — 2 blocker `Stale_release…` và `Crash_after_inventory…` đã chuyển sang Harness. Regression `TicketFlow.slnx` xanh **86/86**: baseline đã tăng từ 81 lên 86 ở Commit 4 (thêm 5 integration test Redis cho CAS release/confirm có owner+generation trong `TicketFlow.Reservation.IntegrationTests`), Commit 5 không đổi số này. Mọi tham chiếu "81/81" ở các commit trước là baseline tại thời điểm đó, không phải con số hiện hành.

### Commit 6 — `feat(payment): idempotent charge/refund state machine + unknown reconciliation` ⭐ ✅ DONE
- Atomically insert/claim `PaymentAttempt` unique `OrderId` trước external call; partition/lock cùng order để hai consumer không charge song song.
- Provider interface nhận idempotency key và hỗ trợ lookup; fake gateway đếm call và mô phỏng response loss sau charge.
- Phân biệt `Declined`, transient retryable và `Unknown`; circuit/timeout không tự biến thành decline hoặc release seat.
- Implement provider `RefundAsync` idempotent và refund states; reconciliation worker có retry/backoff, alert/manual-review sau deadline.
- Chỉ phát `PaymentProcessed`, `PaymentFailed`, `PaymentRefunded` từ outbox sau durable state transition.
- **Verify:** 100 concurrent distinct messages cùng order tạo đúng một provider charge; crash sau provider success không charge lại; ambiguous outcome reconcile đúng; refund gọi provider đúng một lần.

**Kết quả thực tế (2026-07-22):** `PaymentAttempt`/`PaymentRefund` (schema dựng sẵn ở Commit 2, chưa có writer) giờ là state machine thật: `Pending → Authorized | Declined | Unknown`. Thứ tự thao tác là điểm mấu chốt — **claim (insert + commit) → gọi provider bằng chính operation key đó → ghi outcome thật**. Row được commit *trước* lời gọi provider, nên unique index `order_id` mới là thứ chặn double-charge: consumer thua cuộc nhận unique violation rồi replay kết quả của người thắng thay vì charge lần hai. Kiểu cũ (đọc DB rồi mới gọi provider) để cả hai caller đi qua bước kiểm tra trước khi ai kịp ghi gì. Thêm advisory lock theo order (cùng khuôn với receipt repository của Reservation) làm fast path, unique index làm backstop.

**Phân loại outcome trung thực** — lỗi đắt nhất của code cũ: `catch (Exception)` biến *mọi* timeout/circuit-open/mất response thành `Succeeded=false` → `PaymentFailed` → saga release ghế và huỷ một đơn có thể đã bị trừ tiền. Giờ `GatewayChargeOutcome` tách `Authorized`/`Declined`/`Unknown`; chỉ `GatewayDeclinedException` (quyết định thật của provider) mới là Declined, còn lại là Unknown. Consumer gặp Unknown thì **không publish gì và throw** — thà saga đứng ở `AwaitingPayment` (Commit 9 sẽ thêm deadline/ManualReview) còn hơn compensate lên một khoản tiền có thể đã mất. Port gateway thêm `IdempotencyKey` trên mọi lời gọi mutating + `LookupChargeAsync`/`LookupRefundAsync` để giải quyết mập mờ bằng cách **hỏi provider**, không đoán; fake gateway có ledger idempotency đúng như provider thật (Stripe/VNPay) và ghi nhận cả trường hợp "charge thành công nhưng mất response".

**Refund lần đầu tiên gọi provider thật.** Trước commit này `RefundPaymentConsumer` chỉ đổi một cột status local rồi publish `PaymentRefunded` — saga báo đã hoàn tiền trong khi tiền khách chưa hề nhúc nhích. Giờ có `PaymentRefund` state machine + `RefundAsync` idempotent theo key. Thêm `PaymentReconciliationWorker` (interval, batch, deadline → ManualReview có metric + log) để giải quyết Unknown còn tồn và publish nốt event mà consumer đã cố tình giữ lại.

**Điều chỉnh plan có bằng chứng:** (1) **`PaymentTransaction` (Phase 5) không còn là writer** — giữ cả nó lẫn `PaymentAttempt` chính là dual-write bên trong một service, đúng thứ phase này chống. `PaymentAttempt.Id` giờ là `PaymentId` trong contract; bảng cũ giữ nguyên để đọc lịch sử, **không backfill đoán** (đúng quy tắc mục 10). (2) Vì id do hai bảng sinh ra là hai giá trị khác nhau cho cùng một sự việc, **refund định danh theo `OrderId`**, `PaymentId` trong message chỉ là cross-check — nếu không, refund cho đơn Phase 5 sẽ không tìm thấy attempt. (3) Polly pipeline **không còn retry/đếm circuit trên `GatewayDeclinedException`**: thẻ bị từ chối là câu trả lời, không phải sự cố; để nguyên thì đủ nhiều thẻ bị từ chối sẽ mở circuit cho tất cả người khác. (4) Refund `Declined` (không có charge nào authorized) vẫn publish `PaymentRefunded` để saga đi tiếp — ghế nó muốn lấy lại vốn chưa từng được trả tiền; chỉ `Unknown` mới giữ lại event.

**Ghi chú migration:** `Phase12PaymentStateMachine` chỉ additive (cột nullable + int default 0 + index). Việc siết `order_id` thành UNIQUE và đổi tên giá trị enum (`Succeeded→Authorized`, `Failed→Declined`, thêm `Unknown`) an toàn vì Commit 2 tạo hai bảng này ở dạng expand-only **không có writer** — bảng rỗng, không có dữ liệu nào vi phạm được và không có gì để backfill.

**Một phát hiện từ chính test:** bản test đầu dùng rendezvous barrier bên trong lời gọi provider để ép hai consumer va nhau — và nó **deadlock**, vì per-order claim đã serialize đúng hai caller nên không bao giờ có đủ 2 participant ở barrier. Deadlock đó chính là kết quả cần chứng minh; test được viết lại để assert trên **số lần provider bị gọi** thay vì quan sát tính đồng thời ở barrier, và lý do được ghi ngay trong test.

**Gate đã chạy:** hardening trait `Phase12Harness` xanh **58/58** (thêm 7 case payment: hai message phân biệt cùng order chỉ một charge; **100 duplicate đồng thời vẫn đúng một charge**; mất response giữ `Unknown` chứ không thành Declined; reconcile bằng lookup không charge lại; Unknown mà provider không có ghi nhận thì resolve thành Declined; refund trùng chỉ gọi provider một lần; refund cho đơn chưa charge không chạm provider). Trait `Phase12ExpectedFailure` còn **13 đỏ / 3 control xanh** (trước Commit 6: 14 đỏ) — blocker double-charge đã khép. Regression `TicketFlow.slnx` xanh **86/86**, gồm 3 test `PaymentGatewayResiliencePipelineTests` cũ vẫn xanh sau khi pipeline đổi `ShouldHandle` (chúng dùng `InvalidOperationException`, tức nhánh transient, nên vẫn retry/mở circuit đúng như trước).

---

### Audit nhóm B (2026-07-23)

Rà lại Commit 3–6 bằng cách đọc code thật, không tin phần mô tả trong doc — cùng cách làm với audit nhóm C.

**Xác nhận đã đúng:** `POST /holds/{holdId}/confirm` không còn tồn tại và `/release` owner-scope theo `sub`; reconciler có đủ ba mode với `Report` read-only thật; inbox/outbox EF bật trên mọi consumer endpoint còn SignalR cố ý nằm ngoài; `UnsellAsync` đã bị xoá sạch, `MarkSold`/`MarkAvailable` chỉ còn được gọi từ trong confirm/compensate; `PaymentReconciliationWorker` và `ReservationCacheReconciliationWorker` đều thực sự được `AddHostedService`; `/orders/legacy` chỉ map khi `IsDevelopment()`; hold cap/rate limit đếm từ `hold_quotes` bền vững; `MatchesAuthority` so amount/currency/snapshot/generation với quote đã persist, nên số tiền trong message không tự quyết định được gì.

**Lỗ hổng thật audit bắt được — đã sửa.** `ReleaseAllocationScript` — CAS cấp cache mà đoạn kết quả Commit 5 mô tả rất kỹ — **chưa từng được chạy bởi bất kỳ test nào trong repo**. Nguyên nhân là một default interface method: `ISeatHoldStore.ReleaseAllocationAsync` có thân mặc định chuyển tiếp sang `RemoveFromSoldAsync`, tức `SREM` vô điều kiện của thời tiền-Phase-12. Chỉ `RedisSeatHoldStore` override nó; **cả bốn test double đều không**, kể cả `ObservedSeatHoldStore` — decorator bọc một Redis **thật** trong `ReservationDurabilityIntegrationTests`. Nên ba test mang đúng tên bất biến ownership (`Stale_release_from_order_A_must_not_unsell_a_seat_now_owned_by_order_B`, `Owner_scoped_compensation_…`, `Compensation_naming_another_orders_seat_set_…`) đều xanh **chỉ nhờ CAS phía Postgres**; nếu `CompensateAsync` quay về gọi `RemoveFromSoldAsync` — đúng hồi quy mà Commit 5 sinh ra để chống — cả ba vẫn xanh. Bản thân script viết đúng: hai test mới trên Redis thật xác nhận nó ngay lần chạy đầu. Vấn đề là không có gì bảo vệ nó.

Đã sửa theo bước **contract** của mục 10, vì cửa sổ tương thích mà các adapter đó sinh ra đã đóng (production override hết từ Commit 5): bỏ thân mặc định của `TryHoldAsync`/`ReleaseAsync`/`ConfirmAsync`/`ReleaseAllocationAsync` để thiếu override là **lỗi biên dịch**, và xoá hẳn `RemoveFromSoldAsync` khỏi port lẫn `RedisSeatHoldStore` — nó không còn caller production nào và chỉ tồn tại để nuôi chính cái fallback này. Compiler lập tức chỉ đúng bốn double. `FakeSeatHoldStore` giờ mô phỏng đúng CAS (kèm `SeedConfirmed` mang theo tuple sở hữu, thay `SeedSold` vốn chỉ ghi seat id nên cache không có khái niệm *ai* sở hữu ghế đã bán); hai decorator forward sang inner; `RollbackAwareHoldStore` **throw** cho confirm/compensate thay vì bịa ra một thành công, vì nó chỉ mô hình hoá đường hold.

**Dọn dây điện còn cắm điện.** `ReservationService.ReleaseAsync(eventId, holdId)` — public, doc ghi "trusted internal release path", **không kiểm owner** — có **không một caller nào** trong src lẫn test. Đúng hình dạng cái endpoint mà Commit 4 đã xoá, chỉ là chưa ai nối dây vào. Đã xoá.

**Còn mở, và đúng chỗ:** gRPC `GetHoldQuote` lấy `OwnerUserId` từ **request body** chứ không từ token, và `POST /events/{eventId}/inventory` không có `RequireAuthorization` nào — cả hai đều thuộc Commit 14 theo đúng chữ trong plan ("Reservation/Ticketing/Catalog tự validate token; internal calls dùng service principal/audience riêng" và "scope … inventory … theo resource"). Ghi lại ở đây để chúng là khoản nợ có tên, không phải thứ audit bỏ sót.

**Một khoảng trống tài liệu, không phải code.** Commit 4 (`45d4878`) ship đủ nhưng mục của nó trong doc này chưa bao giờ được đánh dấu `DONE` và không có đoạn kết quả/gate nào, trong khi Commit 5 và 7 viện dẫn nó như đã xong. Đã viết bổ sung từ code thật và ghi rõ là viết sau.

**Gate đã chạy:** `Phase12Harness` xanh **126/126** (không đổi số — audit này không thêm case ở đó, nhưng ba test ownership giờ mới thật sự kiểm cả tầng cache). Regression `TicketFlow.slnx` xanh **88/88**, tăng từ 86 nhờ hai test Redis thật mới cho `ReleaseAllocationAsync`: compensation trễ của order A không đụng ghế đã bán lại cho order B (`OwnershipChanged`, ghế vẫn `Sold`), và sai owner/generation/order đều là stale còn replay đúng tuple là `AlreadyReleased`.

---

## Nhóm C — Ordering/process manager: một writer, có timeout, có recovery

### Commit 7 — `feat(ordering): idempotent order initiation + durable Marten relay/outbox` ✅ DONE
- Persist order stream, checkout operation record và đủ saga-initiation data trong cùng transaction. `OrderPlaced`/event mới phải có `HoldId`, authoritative allocation/quote reference, payment method và contact cần thiết để relay lại.
- Unique checkout idempotency key + unique hold-to-order; retry trả original `OrderId` nếu request hash giống, trả `409` nếu khác.
- Dispatcher publish từ durable outbox/checkpoint, không publish trực tiếp sau `SaveChanges`. Crash trước/sau broker ack đều an toàn.
- **Verify:** kill sau Marten commit trước publish; restart dispatcher khởi động đúng một process về business outcome; double-submit trả cùng order.

**Kết quả thực tế (2026-07-22):** Lỗi cũ rất gọn: handler mint `Guid.NewGuid()` mới mỗi lần gọi, nên retry sau khi publish hỏng đẻ ra **order thứ hai cho cùng một hold**. Giờ có `CheckoutOperation` — biên nhận bền, id = `{CustomerId}:{IdempotencyKey}` — cộng `OrderDispatch` (outbox, id = `OrderId`), cả hai commit **cùng một transaction Marten** với order stream. Handler **không còn publish gì cả**; `OrderDispatchRelay` (BackgroundService) là nơi duy nhất đẩy `OrderSubmitted` lên RabbitMQ, nên broker chết không còn bỏ lại order Pending mà không saga nào nhặt.

**Ba lớp chặn trùng, không phải một:** (1) id document = khoá idempotency theo customer; (2) **unique index trên `HoldId`** — chặn client tự sinh key mới mỗi lần retry, thứ mà lớp (1) hoàn toàn không thấy; (3) `Insert` chứ **không** `Store`. Điểm (3) là bẫy thật của Marten: `Store` là **upsert**, nên request thua cuộc sẽ *ghi đè* biên nhận của người thắng và trỏ khoá sang order thứ hai — đúng thứ bản ghi này sinh ra để chặn. Có test riêng trên Postgres thật cho từng lớp.

**Điều chỉnh plan có bằng chứng:** (1) Plan viết "`OrderPlaced`/event mới phải có `HoldId`, payment method và contact" — **không nhét vào `OrderPlaced`**. Payment method, tên và email người mua là mối quan tâm của transport/notification, không phải trạng thái đơn hàng; chính doc comment cũ của `PlaceOrderCommand` đã nêu đúng lý do đó. Payload đầy đủ nằm nguyên vẹn trong outbox row, nên relay dựng lại đúng message đã quyết ở checkout thay vì ghép lại từ các mảnh rời. (2) **`PlaceOrderCommandHandler` (legacy) không còn là writer thứ hai** — trước đây nó tự mint order id và publish thẳng, tức là hai writer với hai mức bảo đảm khác nhau, đúng bài dual-write. Giờ nó đi qua cùng `CheckoutAcceptanceService`, chỉ khác ở chỗ các con số lấy từ đâu; khoá idempotency suy ra từ `HoldId` vì contract legacy không có khoá riêng. (3) Cấu hình Marten tách thành `ServiceCollectionExtensions.Configure(StoreOptions)` để test tích hợp dùng **đúng cấu hình production**, không phải bản chép tay — một unique index chỉ tồn tại trong bản chép thì không chứng minh được gì.

**Một cạnh sắc phát hiện khi soi lại:** `SaveChangesAsync` hỏng **không** dọn unit of work — các insert đã stage vẫn nằm trong session. Nhánh xử lý race gọi tiếp truy vấn trên chính session đó, nên nếu có ai gọi `SaveChanges` lần nữa trong cùng scope thì insert vừa thua race sẽ được thử lại. Đã thêm `DiscardPendingChanges()` (`EjectAllPendingChanges` của Marten, xác nhận tồn tại bằng probe reflection trước khi dùng) vào port và gọi ngay khi bắt được unique violation.

**Gate đã chạy:** hardening trait `Phase12Harness` xanh **69/69** (58 → 69, thêm 11 case checkout). Bảy case chạy trên fake nhưng nhắm đúng quyết định của service (retry sau publish hỏng dùng lại một stream; handler không publish gì trước khi relay chạy; double-submit trả cùng order; **20 submit đồng thời vẫn một order**; key mới cho cùng hold không tạo order thứ hai; key cũ dùng cho request khác bị từ chối; relay quét 3 lần vẫn publish đúng một lần). Bốn case còn lại chạy trên **Postgres thật** qua `HardeningEnvironmentFixture` và dùng đúng cấu hình Marten production: unique index `HoldId` chặn hai checkout cùng hold *ở tầng database*, `Insert` từ chối trùng khoá thay vì upsert đè lên người thắng, receipt hỏng thì **order stream và outbox row cũng không tồn tại**, và 12 checkout đồng thời để lại đúng một order.

Trait `Phase12ExpectedFailure`: **11 đỏ / 4 xanh trên 15** (trước Commit 7: 13 đỏ / 3 xanh trên 16). Tổng giảm 1 vì test checkout được promote sang Harness — **đó là toàn bộ phần Commit 7 thực sự khép**. Chênh lệch còn lại là **flake, không phải cải thiện**: `PurchaseSagaMessageOrderTests` báo 5 đỏ trong batch, nhưng chạy riêng thì **6/6 đều đỏ, lặp lại ổn định qua 2 lần** — một permutation chỉ xanh nhờ nhiễu giữa các test dùng chung RabbitMQ/Marten fixture. Ghi lại ở đây để Commit 8 xử lý cả hai: bản thân bug ordering, và việc suite bị phụ thuộc thứ tự chạy. Regression `TicketFlow.slnx` xanh **86/86**.

**Về đảm bảo giao hàng — nói thẳng:** relay publish trước, đánh dấu `Dispatched` sau. Crash đúng khe đó thì lần quét sau publish lại, tức **at-least-once**. Đảo thứ tự lại còn tệ hơn (mất hẳn message), và không thứ tự nào giữa hai hệ thống cho ra exactly-once. Saga chịu được vì `OrderSubmitted` correlate theo `OrderId` và chỉ khởi tạo ở `Initially`; Commit 8 (inbox theo `OrderId`) mới là chỗ biến "chịu được" thành "được khẳng định". Nhiều replica dùng `pg_try_advisory_lock` không chặn — replica khác đang giữ order nào thì bỏ qua order đó, không xếp hàng chờ rồi publish trùng.

### Commit 8 — `refactor(ordering): single per-order milestone writer + expected stream version + inbox` ✅ DONE
- Participant response và admin command vào một queue/partition theo `OrderId`; process manager là writer duy nhất của lifecycle stream.
- Append dùng expected version/fetch-for-writing; conflict reload + re-decide, không blind append. Domain transition table từ chối regression và late terminal event.
- Xóa/disable các independent audit consumer chỉ append milestone sau compatibility drain; projection guard vẫn giữ defense-in-depth nhưng không còn che write-model sai.
- Admin cancel ở từng state phát compensation đúng; force-confirm không bypass paid/seat/ticket invariant.
- **[Delta review 2026-07-20]** Inbox/single-writer ở đây khép luôn 2 lỗi nhỏ đã xác minh trong code: (a) `OrderCompletedConsumer` tăng metric `ticketflow.tickets.sold` NGOÀI transaction — redelivery đếm trùng "vé bán/phút" (sai metric, không sai tiền); (b) milestone append trùng làm bẩn audit stream (Marten không có inbox, chỉ có monotonic guard cứu display). Cả hai cần test case gọi tên riêng trong permutation suite.
- **[Delta review 2026-07-20]** `MyOrdersProjection.Apply(OrderTicketsIssued)` đang bỏ qua `TicketPdfUrls` (backlog Phase 9 chưa vào matrix) — khi rebuild projection theo canonical state ở commit này, thêm PDF object keys vào `MyOrderView` để "vé của tôi" không phải gọi Ticketing chỉ để lấy link PDF.
- **Verify:** chạy toàn bộ permutation/duplicate/fan-in song song trên RabbitMQ + Marten thật; stream luôn monotonic, projection rebuild ra cùng kết quả (gồm PDF keys); metric vé bán không đếm trùng dưới redelivery.

**Kết quả thực tế (2026-07-22).**

**Single writer.** Sáu consumer độc lập (`OrderSeatsConfirmed`/`OrderPaymentAuthorized`/`OrderCompleted`/`OrderPaymentRefunded`/`OrderSeatsReleased`/`OrderCancelled`) đã bị xoá, thay bằng **một** `OrderMilestoneConsumer` trên một receive endpoint, ghi qua **một** `OrderMilestoneWriter`. Trước đây mỗi consumer có queue riêng nên sáu cái có thể cùng ở trong một stream, mỗi cái `Load` rồi `Append` mù — hai cái cùng fold version N thì cùng append, đó là cách một order có hai terminal event. Giờ append đi qua `FetchForWriting` (Marten giữ version đã đọc, kiểm tra lúc `SaveChanges`); thua race thì **reload + quyết định lại**, không retry chính append cũ — vì người thắng có thể đã làm đúng việc đó rồi, và lần quyết định lại sẽ đúng đắn kết luận "không cần làm gì". Kèm **bảng chuyển trạng thái** trong `Decide`: milestone trễ không kéo được order đã terminal đi ngược, milestone đã có trong stream thì bỏ qua.

**Saga hội tụ theo mọi thứ tự.** Mỗi milestone giờ là một **fact có timestamp** trên instance (`SeatsConfirmedAt`/`PaymentAuthorizedAt`/`TicketsIssuedAt`, cộng `*RequestedAt` để mỗi command chỉ phát một lần), bind bằng `DuringAny` thay vì `During(state)`, và `Advance` quyết định từ các fact đang có. Trước đây milestone đến sai state thì **không khớp handler nào và bị bỏ im lặng**, không gì redeliver — cả 6 thứ tự giao đều treo, kể cả thứ tự tự nhiên, vì test gửi mỗi fact hai lần không nghỉ nên bản thứ hai có thể vượt qua transition thứ nhất. `SetCompletedWhenFinalized` bị bỏ: instance đã xoá thì không nhận ra được bản trùng, nên giữ instance ở state terminal mới diễn đạt được "đã xong rồi, bỏ qua" (lưu trữ/dọn dẹp instance terminal thuộc Commit 9).

**Điều chỉnh plan có bằng chứng:** (1) Saga được partition theo `OrderId` đúng như plan yêu cầu, qua `PurchaseSagaDefinition` (`endpointConfigurator.CreatePartitioner(32)` + `sagaConfigurator.Message<T>(x => x.UsePartitioner(...))`) cộng `UseInMemoryOutbox` để giữ message đi ra cho tới khi Marten commit xong version. Endpoint milestone thì **không** partition, và đó là cố ý: bảng chuyển trạng thái độc lập thứ tự, version check biến thua race thành quyết định lại chứ không phải append đôi. (2) `Initially` và `DuringAny` **cùng bind `OrderSubmitted` là xung đột** — `DuringAny` bao gồm cả `Initial`, nên no-op có thể che mất handler khởi tạo thật; đã liệt kê state theo vòng lặp `During(state, ...)`. (3) Bật `UseOptimisticConcurrency(true)` cho saga repository ở **cả `Program.cs` lẫn `HardeningEnvironmentFixture`** — fixture trước đó **không** dùng cấu hình production, tức test đang đo một cấu hình không ai chạy; đó là lỗi test thật.

**Hai mục delta đã khép:** (a) `ticketflow.tickets.sold` chỉ tăng khi commit **thực sự append** terminal transition — trước đây tăng sau *mọi* delivery, ngoài quyết định, nên redelivery thổi phồng "vé bán/phút" cho đơn chỉ bán một lần; (b) `MyOrderView` có `TicketPdfUrls`, "vé của tôi" không phải gọi Ticketing chỉ để lấy link PDF. Monotonic guard trong `MyOrdersProjection` hạ xuống **defense-in-depth** kèm ghi chú trung thực: nó từng che một write stream thật sự sai thứ tự (sửa hiển thị trong khi audit trail vẫn sai), giờ nguyên nhân đã bị loại bỏ.

**Ba lỗi trong chính test, đáng ghi lại nhất là lỗi thứ ba.** (1) Fake đầu tiên giữ staged writes trong `AsyncLocal` trên store dùng chung — chúng biến mất im lặng, vì **giá trị gán cho `AsyncLocal` bên trong một async method không truyền ngược về caller**; viết lại thành backend dùng chung + store **scoped**, đúng như production. (2) Assertion `WaitUntilAsync(() => count >= 2)` được viết để *chờ hành vi lỗi xuất hiện*, nên hệ thống đã đúng chỉ có thể làm nó timeout. (3) **Assertion sai làm tôi tưởng saga hỏng**: bản đầu đòi `IssueTickets == 1` cho mọi permutation, nhưng nếu `TicketsIssued` đến trước khi thanh toán được duyệt thì vé **đã tồn tại** — đòi phát vé lần nữa mới là bug. Năm trên sáu permutation "đỏ" trong khi state machine chạy đúng. Chỉ vỡ ra khi dựng suite in-memory tất định rồi **dump thẳng saga instance** (`state=Completed`, `pay=1 iss=0 done=1`) thay vì suy đoán qua RabbitMQ. Bất biến đúng là: **completion đúng một lần, participant command nhiều nhất một lần**.

**Một đính chính, vì tôi đã ghi sai vào chính doc này.** Bản trước của mục Commit 8 khẳng định "MassTransit 8.5 không có `UsePartitioner` trong assembly core, đã probe reflection". **Sai.** Probe chỉ quét các method static public tên `UsePartitioner` trong static class của assembly core, nên bỏ sót đúng cách dùng thật: `endpointConfigurator.CreatePartitioner(n)` rồi `sagaConfigurator.Message<T>(x => x.UsePartitioner(partitioner, m => m.Message.OrderId))`. Một probe âm tính chỉ chứng minh được "không tìm thấy theo cách đã tìm", không chứng minh được "không tồn tại" — và tôi đã trình bày nó như vế thứ hai.

Đính chính này cũng giải thích một điều trước đó tôi ghi là "không chắc": suite permutation trên RabbitMQ đổi từ đỏ sang xanh mà **không có thay đổi code production nào từ phía tôi**. Lý do là `PurchaseSagaDefinition` được thêm vào ngoài phiên làm việc của tôi, partition mọi message type theo `OrderId` và bọc `UseInMemoryOutbox` — chính thứ serialize các delivery trùng cho cùng một order và chặn một execution thua cuộc publish trước khi `SaveChanges`. Callback `UseOptimisticConcurrency(true)` tôi thêm vào `MartenRepository()` đã bị gỡ, kèm lý do: `MartenRepository` tự bật optimistic concurrency cho saga document, còn callback đó không ngăn được publish sớm. Bằng chứng vững nhất vẫn là **suite in-memory tất định xanh cả 6 permutation** (không phụ thuộc broker), còn RabbitMQ xác nhận nó sống sót qua transport thật.

**Một khoảng trống được để đỏ có chủ ý.** `A_milestone_arriving_before_the_saga_exists_must_not_be_lost` giữ ở `Phase12ExpectedFailure`: chỉ `Initially` mới tạo được instance, nên milestone đến trước khi instance tồn tại vẫn không khớp correlation và bị bỏ. Đúng phạm vi Commit 9 ("missing-instance response được scheduled redelivery thay vì drop") — để đỏ cho khỏi phải phát hiện lại.

**Gate đã chạy:** `Phase12Harness` xanh **88/88** (74 → 88). Thêm 5 case milestone (hai `OrderCompleted` phân biệt cùng lúc chỉ append một terminal transition; redelivery không đếm sale hai lần; milestone trễ không kéo ngược đơn đã terminal; milestone cho order lạ không tạo stream ma; version conflict thì reload + quyết định lại) và 9 case saga, trong đó **6 permutation trên in-memory harness tất định** cộng 3 case bất biến (duplicate `OrderSubmitted` không khởi động mua hai lần; `TicketingFailed` đến trễ không mở lại đơn đã hoàn tất) — và **6 permutation cũ trên RabbitMQ + Marten thật được promote từ `ExpectedFailure` sang `Harness`**.

`Phase12ExpectedFailure` còn **6 đỏ / 3 xanh trên 9** (trước Commit 8: 11 đỏ / 3 xanh trên 14): −6 permutation saga đã khép, +1 khoảng trống Commit 9 nêu trên. Regression `TicketFlow.slnx` xanh **86/86**.

### Commit 9 — `feat(ordering): saga deadlines, canonical completion, transport relays & recovery tooling` ⭐ ✅ DONE
- Mỗi state `Awaiting*`/`Compensating*` có scheduled deadline và explicit transition: retry safely, compensate, `Unknown`, hoặc `ManualReview`; không state vô hạn im lặng.
- Đảm bảo saga/process state + outgoing command outbox commit trước broker send; missing-instance response được scheduled redelivery thay vì drop.
- Persist canonical `OrderCompleted` + stable `CompletedAt` trước; RabbitMQ notification và Kafka analytics relay độc lập với checkpoint/dedup.
- Thêm operator query/CLI/API tối thiểu để list stuck/unknown, inspect operation history, retry relay/reconcile an toàn; DLQ có alert và runbook redrive.
- **[Delta review 2026-07-20]** Thay retry policy toàn cục 3×2s (`TicketFlow.Messaging` áp cho MỌI consumer) bằng policy phân theo loại lỗi: transient infra (backoff dài hơn), out-of-order guard throw (redelivery phải giãn đủ để milestone trước kịp đến — 3×2s hiện tại gần như chắc chắn đẩy `OrderCompletedConsumer` vào `_error` queue phải redrive tay), permanent (DLQ ngay). Partition theo `OrderId` ở Commit 8 xóa phần lớn out-of-order, nhưng policy vẫn phải đúng cho phần còn lại.
- **Verify:** participant silence, Rabbit/Kafka outage, process restart và duplicate timeout đều hội tụ; Kafka down không ngăn paid+ticketed order thành `Confirmed`; không phát analytics sale hai lần.

**Kết quả thực tế (2026-07-23).**

**Deadline.** Mỗi state `Awaiting*`/`Compensating*` giờ có deadline 5 phút. Hết hạn mà participant im lặng thì saga **gửi lại chính lệnh đang chờ** — an toàn vì mọi lệnh participant đều idempotent (Commit 5–7) — tối đa 3 lần, rồi chuyển sang `ManualReview` và **dừng hẳn**. `ManualReview` là terminal với state machine nhưng không phải với đơn hàng: nó nghĩa là automation đã dừng và có người chịu trách nhiệm. Điểm này quan trọng: **deadline không bao giờ tự huỷ đơn**. Huỷ vì im lặng sẽ nhả ghế của một giao dịch có thể đã thanh toán — đúng loại lỗi mà Commit 6 sinh ra để chống.

**Thiết kế token thay vì cancel.** Deadline mang một `Token`; saga giữ token của lần chờ hiện tại. Deadline có token không khớp là cũ (participant đã trả lời, hoặc đã re-arm) và bị bỏ qua. Chọn cách này vì **cancel là một cuộc đua**: message có thể đã bay đi khi câu trả lời về. Nhận diện deadline cũ lúc nó đến thì quyết định được; ngăn nó được gửi thì không. Toàn bộ arm/cancel gom vào `TransitionAsync` — điểm duy nhất mọi nhánh đi qua — nên không nhánh nào quên được.

**Kafka rời khỏi critical path.** Saga từng gọi thẳng Kafka producer trong lúc hoàn tất đơn: Kafka chết là activity throw và đơn **đã trả tiền, đã có vé** không bao giờ thành `Confirmed`. Giờ nó publish `RelaySaleRecorded` qua RabbitMQ (đi qua in-memory outbox của saga, nên chỉ gửi sau khi saga state commit), và `SaleRelayConsumer` xử lý với retry/DLQ riêng. Consumer dùng claim-before-produce như Payment Commit 6, nhưng **chỉ đánh dấu hoàn tất sau khi Kafka nhận** — bản đầu tôi viết sai chỗ này: claim xong mà produce hỏng thì retry sẽ thấy "đã claim" rồi bỏ qua, **mất luôn sale**. Đảm bảo trung thực: không bao giờ hai sale do delivery đồng thời hay produce hỏng, nhưng crash đúng khe hẹp giữa produce thành công và lúc đánh dấu sẽ phát lại một lần. Đóng khe đó cần idempotent producer key mà topic analytics không có; ghi ra còn hơn ngụ ý exactly-once.

**Retry policy phân loại.** Thay 3×2s toàn cục bằng hai tầng khác cơ chế: retry ngay (200ms/500ms/2s, in-process, giữ consumer) chỉ cho sự cố hạ tầng thoáng qua; delayed redelivery (5s → 5m, trả message về broker, nhả consumer) cho message **đến sớm** — thứ mà 6 giây retry không bao giờ đủ để milestone trước kịp tới, nên policy cũ đẩy thẳng nó vào `_error` phải redrive tay. `PermanentMessageFaultException` bị loại khỏi cả hai: retry một fault vĩnh viễn chỉ làm chậm cái DLQ entry mà operator cần để nhận ra vấn đề.

**Missing-instance redelivery** (khép nốt test đỏ còn lại của Commit 8). Chỉ `Initially` tạo được instance, nên câu trả lời participant vượt mặt `OrderSubmitted` khớp không correlation nào và bị vứt. Giờ `OnMissingInstance(...).Redeliver(...)` lên lịch gửi lại (1s → 30s) rồi mới `Fault()`, nên `OrderId` lạ thật vẫn vào DLQ chứ không lặp vô hạn.

**Operator API + runbook.** `GET /ops/orders/stuck`, `GET /ops/orders/{id}`, `POST /ops/orders/{id}/resume`, kèm [docs/runbook-stuck-orders.md](runbook-stuck-orders.md). Cố ý hẹp: **không có force-complete, không có force-cancel** — cả hai đều cho phép operator khẳng định một kết quả mà participant chưa từng xác nhận. Thao tác ghi duy nhất là gửi lại lệnh saga vốn đã nợ. Runbook có một bước bắt buộc trước khi retry đơn kẹt ở thanh toán: **kiểm tra `PaymentAttempt` trước** — nếu `Unknown` thì tiền có thể đã trừ, phải chờ reconcile.

**Một phát hiện hạ tầng thật.** `AddDelayedMessageScheduler` cần plugin `rabbitmq_delayed_message_exchange`, mà image `rabbitmq:3-management` **không có**. Không có nó, redelivery im lặng không làm gì và message bị vứt y như cũ — test trên broker thật thất bại đúng như vậy trước khi đổi. Đã chuyển `docker-compose.infra.yml` và Helm values sang `masstransit/rabbitmq` (chính image đó + plugin, xác nhận bằng `rabbitmq-plugins list`: v4.3.1).

**Gate đã chạy:** `Phase12Harness` xanh **96/96** (88 → 96). Thêm 7 case deadline (participant im lặng thì lệnh được gửi lại; deadline cũ bị bỏ qua chứ không tính là im lặng; hết lượt thử thì escalate sang ManualReview mà **không** huỷ đơn, không nhả ghế; đơn ManualReview ngừng arm deadline mới; operator resume đưa đơn trở lại automation; resume **không** mở lại đơn đã hoàn tất; hoàn tất đơn thì bàn sale cho relay chứ không produce inline) và 1 case missing-instance redelivery **trên RabbitMQ thật** — cố ý không chạy in-memory, vì cơ chế nằm ở plugin delayed-message-exchange mà harness in-memory không có, nên bản in-memory sẽ chẳng chứng minh được gì.

`Phase12ExpectedFailure` còn **5 đỏ / 3 xanh trên 8** (trước Commit 9: 6 đỏ / 3 xanh trên 9) — **không còn cái nào thuộc nhóm C**; 5 cái còn lại là Catalog lifecycle (3) và Ticketing (2), thuộc nhóm D. Regression `TicketFlow.slnx` xanh **86/86**.

**Ba lỗi trong test tôi tự gây ra, ghi lại vì đều là bẫy chung.** (1) `harness.InactivityTask` là **one-shot**: đã complete rồi thì mọi await sau trả về ngay, nên test bắn deadline liên tiếp đọc saga trước khi message được xử lý và thấy strike đứng yên ở 1 — phải chờ theo tín hiệu thật (strike tăng). (2) Harness in-memory không đăng ký `PurchaseSagaDefinition`, tức đo một saga endpoint không ai chạy — cùng loại divergence đã che hai bug thật trong suite này. (3) `UseScheduledRedelivery` xử lý **exception**, không xử lý missing-instance; đúng API là `OnMissingInstance` trên event của saga — tôi đã viết nhầm và phải sửa lại.

---

### Audit nhóm C (2026-07-23)

Rà lại Commit 7–9 bằng cách đọc code thật, không tin phần mô tả trong doc.

**Xác nhận đã đúng:** không còn `Interval(3, TimeSpan.FromSeconds(2))` ở đâu trong `src/`; saga không còn tham chiếu `ISaleRecordedPublisher`/`SaleRecorded`; mọi milestone consumer đã gộp về một `OrderMilestoneConsumer` duy nhất.

**Một lỗ hổng thật audit bắt được — đã sửa.** `ConfirmOrderCommandHandler` và `CancelOrderCommandHandler` (đường admin override) **vẫn `Load` rồi `Append` mù** vào order stream — đúng cái race Commit 8 đã xoá khỏi các milestone consumer, chỉ khác là ở đường admin nên không consumer test nào chạm tới. Một operator confirm trong lúc saga đang hoàn tất cùng đơn đó có thể append **hai terminal event**. Đã chuyển cả hai sang `FetchForWritingAsync`; thua race giờ hiện ra thành guard của `Order.Confirm` từ chối, chứ không thành transition thứ hai.

**Một test cũ được sửa cho khớp thiết kế mới, không phải để nó xanh.** `PurchaseSagaTests.Happy_path_...` khẳng định `SetCompletedWhenFinalized` xoá instance và saga publish `SaleRecorded` trực tiếp — cả hai đều đã cố ý thay đổi ở Commit 8/9. Assertion mới: instance ở lại state `Completed` (instance đã xoá thì không nhận ra bản trùng), và sale đi qua `RelaySaleRecorded` với `_saleRecorded.Published` rỗng — tức chứng minh đúng điều Commit 9 cần: **saga không còn chạm Kafka**.

---

## Nhóm D — Ticketing, projection và các workflow còn lại

### Commit 10 — `feat(ticketing): durable issuance + owner-scoped access + atomic event check-in` ⭐ ✅ DONE
- Persist `TicketIssuanceJob` unique `OrderId` trước render; object key deterministic theo ticket/issuance version; transient Lambda/MinIO/DB error throw để retry, terminal failure chỉ sau policy/reconcile.
- Lưu `CustomerId`, `EventId`, QR credential/version, revocation state. Ticket list/PDF query scope theo JWT subject; admin override explicit.
- Check-in dùng atomic conditional update `WHERE CheckedInAt IS NULL AND EventId = expected AND RevokedAt IS NULL`; gate user phải được assign event/gate.
- Reconciliation dọn orphan PDF, hoàn tất row thiếu, không emit `TicketingFailed` chỉ vì unique race với issuer khác.
- **Verify:** partial multi-ticket render, duplicate issue, owner khác download, wrong-event scanner, refunded/revoked ticket và 100 scan đồng thời đều có kết quả xác định; đúng một scan `Valid`.

**Kết quả thực tế (2026-07-23).**

**Check-in nguyên tử.** Endpoint cũ làm read → mutate → `SaveChanges`, không có điều kiện nào ở tầng DB: hai scanner ở hai cổng cùng đọc `CheckedInAt == null` và **cả hai cùng nghe "Valid"** — đúng cách một vé cho hai người vào. Giờ dùng một `UPDATE ... WHERE` duy nhất qua `ExecuteUpdateAsync`, nêu đủ mọi điều kiện (chưa check-in, đúng event, chưa thu hồi); **số dòng thay đổi chính là câu trả lời**: 1 nghĩa là caller này thắng, 0 nghĩa là ai đó đã làm vé không còn hợp lệ. `EvaluateScan` chỉ chạy trước để có thông báo cụ thể — "vé của sự kiện khác" và "vé đã bị thu hồi" là hai câu chuyện khác nhau ở cổng soát vé, không thể gộp thành một chữ "invalid".

**Issuance bền vững.** Guard duy nhất trước đây là "order này đã có vé chưa?", mà điều đó chỉ đúng **sau khi** chạy thành công — hai delivery đến cùng lúc đều thấy chưa có gì và **đều render + upload toàn bộ PDF**. Giờ `TicketIssuanceJob` unique theo `OrderId` được insert và commit **trước khi render**, nên kẻ thua cuộc bị database từ chối trước khi kịp gọi renderer. Object key vẫn deterministic theo `(OrderId, SeatId)` nên retry ghi đè đúng object thay vì để lại orphan.

**Phân loại lỗi — chỗ này tốn tiền thật.** `catch (Exception)` cũ publish `TicketingFailed` cho *mọi* lỗi, nên một cú hiccup của MinIO hay Lambda cold-start timeout sẽ bảo saga **hoàn tiền khách và nhả ghế** — cho một đơn mà PDF rất có thể đã upload xong. Giờ chỉ `PermanentMessageFaultException` mới fail đơn; mọi thứ khác throw để redeliver, job giữ nguyên `Pending`. Cùng một sự phân biệt Commit 6 đã vạch giữa "thẻ bị từ chối" và "mất response".

**Thu hồi vé khi hoàn tiền.** Trước đây **không có gì** thu hồi vé cả: đơn đã hoàn tiền vẫn có PDF hợp lệ và vẫn vào cổng được — tiền trả lại mà ghế cũng mất. `RevokeTicketsConsumer` xử lý `OrderCancelledIntegrationEvent`. Thu hồi chứ **không xoá**: nhân viên cần thấy "đã thu hồi", không phải "không có vé này", để phân biệt hoàn tiền với vé giả.

**Owner-scoped access.** Trước đây bất kỳ user đã đăng nhập nào cũng liệt kê được vé của *bất kỳ* order nào chỉ bằng cách đoán id — endpoint chưa bao giờ hỏi order đó của ai. Giờ scope theo `sub` claim, admin bypass, trả **404 chứ không 403** (403 sẽ xác nhận order tồn tại và thuộc về người khác — bản thân nó là một rò rỉ). Vé cũ không có `CustomerId` vẫn đọc được: khoá cứng chúng lại sẽ khiến chính khách đã mua không lấy được vé của mình.

**Một điều chỉnh nhỏ với lý do:** `ValidateTicketRequest` giờ **bắt buộc** có `EventId`. Không có nó server không thể phân biệt vé tối nay với vé tháng sau, và một scanner chĩa nhầm cổng sẽ vui vẻ cho cả hai vào.

**Gate đã chạy:** `Phase12Harness` xanh **103/103** (96 → 103). Thêm 4 case check-in **trên Postgres thật** (2 scan đồng thời chỉ một Valid; **100 scan đồng thời vẫn đúng một Valid**; vé của event khác bị từ chối và **không** bị đánh dấu check-in; vé đã thu hồi không bao giờ được vào) và 3 case issuance (mất response sau khi upload **không** còn publish `TicketingFailed`; lỗi renderer transient được retry chứ không fail đơn; từ chối vĩnh viễn thì fail đơn đúng một lần).

`Phase12ExpectedFailure` còn **3 đỏ / 3 xanh trên 6** (trước Commit 10: 5 đỏ / 3 xanh trên 8) — cả 3 đều là `CatalogLifecycleOrderingTests`, tức **toàn bộ phần Ticketing đã khép**, chỉ còn nhóm D phần Catalog (Commit 11). Regression `TicketFlow.slnx` xanh **86/86**.

**Một flake do chính tôi tạo ra, đã sửa đúng cách.** `A_manual_review_order_must_stop_arming_further_deadlines` (Commit 9) xanh khi chạy riêng 3/3 nhưng đỏ trong lần chạy cả suite — timeout 5s quá chặt khi 103 test dùng chung một máy. Đã nới thành 30s kèm ghi chú: đây là chờ một quyết định saga làm ngay lập tức, nên hệ thống đúng vẫn thoả tức thì; phần dư chỉ để chịu tải. Sửa timeout, **không** nới lỏng assertion.

### Commit 11 — `feat(catalog,search,notification): optimistic lifecycle + revision-aware projections` ✅ DONE
- Catalog dùng concurrency token/`xmin`, unique `(EventId, SectionId)` và section-name constraints; concurrent Publish/Cancel/Reschedule trả conflict/retry thay vì last-write-wins.
- Lifecycle contracts mang aggregate revision/tombstone; Search và Notification upsert cả cancel/reschedule khi document chưa tồn tại, chỉ nhận revision lớn hơn.
- Kiểm tra Elasticsearch response validity; outage/429/5xx đi retry/DLQ, không giả thành empty/not-found.
- Có replay/rebuild command từ Catalog canonical state; old v1 event revision 0 không overwrite v2.
- **Verify:** mọi permutation/duplicate và concurrent catalog command; cancel không bao giờ resurrect, reschedule mới nhất thắng, replay tạo cùng index/read model.

**Kết quả thực tế (2026-07-23).**

**Catalog: hết last-write-wins.** `xmin` đã được cấu hình sẵn từ trước nhưng **chưa ai dùng** — `DbUpdateConcurrencyException` không được bắt ở đâu cả. Giờ `UnitOfWork` dịch nó thành `ConcurrencyConflictException` ở biên port (Application không phụ thuộc kiểu EF), endpoint trả **409**. Cố ý **không** tự retry: quyết định của request thua có thể đã sai so với việc người thắng vừa làm — ví dụ một organizer huỷ event trong khi người khác đổi lịch, retry mù sẽ áp lịch mới lên một event đã huỷ. Caller phải tải lại và quyết định lại.

**Hai unique index là backstop cho guard in-memory.** `Event.AddPriceTier` và `SeatMap.AddSection` đều kiểm tra trùng bằng dữ liệu **đã load** — hai request đồng thời mỗi cái không thấy row chưa lưu của cái kia, cả hai cùng qua guard. Thêm unique `(EventId, SectionId)` và `(SeatMapId, Name)`, cộng một **functional index `lower(name)`** bằng raw SQL vì EF không có API fluent cho functional index — index thường chỉ bắt trùng y hệt, không khớp với so sánh `OrdinalIgnoreCase` của domain.

**Search/Notification: revision + tombstone.** `AggregateVersion` đã có trên contract từ Commit 2 nhưng **chưa consumer nào đọc**. Hai lỗ hổng thật: (1) một `Published` đến muộn ghi đè lên document đã bị huỷ — **event huỷ sống lại trong kết quả tìm kiếm**; (2) `Rescheduled`/`Cancelled` vượt mặt `Published` thì không có document để update, **mất luôn sự kiện đó vĩnh viễn**. Giờ cả ba lifecycle event đều đi qua `Apply*` vừa guard theo revision vừa tự tạo document nếu chưa có. Cancel **không** guard revision: `Event.Cancel()` là cửa một chiều trong domain, nên tombstone luôn thắng — không message tương lai nào có thể phủ nhận nó một cách hợp lệ.

**Xoá → tombstone, hành vi không đổi.** Trước đây Cancel gọi `DeleteAsync`: xoá thì quên luôn event từng tồn tại, nên `Published` đến sau tạo lại document. Giờ tombstone — giữ document để nhớ, nhưng `GetByIdAsync`/`SearchAsync` đều loại nó ra, nên "biến mất khỏi kết quả tìm" vẫn đúng như quyết định kiến trúc Phase 6.

**Compare-and-swap đặt trong DB, không trong C#.** Elasticsearch dùng **Painless script** trong `_update`: ES chạy script nguyên tử trên đúng một document, nên so-sánh-rồi-quyết-định xảy ra ở server, không ai chen vào được. GET rồi quyết định trong C# rồi mới ghi chính là khe hở read-then-write đã cho hai scanner cùng cho một vé vào cổng ở Commit 10. MongoDB dùng cơ chế khác: filter có `IsCancelled != true` và `Revision <= incoming` (hoặc `Revision` chưa tồn tại — xem lỗi rolling-upgrade bên dưới), nên document không thoả sẽ **không match**, và upsert khi không match sẽ cố insert theo id trong filter → **E11000 duplicate key**. Bắt lỗi đó chính là compare-and-swap, không phải né lỗi — đó là cách MongoDB nói "trạng thái hiện tại từ chối ghi này".

**Kiểm tra response ES.** Mọi method trước đây đọc thẳng kết quả mà không xét `IsValidResponse`. Outage/429/5xx đều cho `Total == 0` — **không phân biệt được với "không có kết quả"**: khách trong lúc ES sập sẽ thấy "không tìm thấy sự kiện nào" thay vì lỗi, và consumer sẽ coi ghi hỏng là ghi thành công rồi không bao giờ retry. Giờ throw `SearchIndexUnavailableException`. Riêng GET trả 404 vẫn là câu trả lời hợp lệ ("không có document"), đã tách riêng để không nhầm thành lỗi.

**Replay.** `POST /admin/events/replay` phát lại canonical state hiện tại của mọi event Published/Cancelled, mang đúng `AggregateVersion` thật — tức đi qua **cùng đường revision-guard** như message live. Nên replay chỉ có thể hoà hoặc thua với thứ consumer đã có, không bao giờ resurrect được event đã huỷ, và message v1 kẹt ở revision 0 không bao giờ thắng nổi replay.

**Ba lỗi production chỉ lộ ra khi chạy thật, không lỗi nào in-memory test bắt được.** Cả ba đều nằm trong chính guard mà commit này dựng lên, và đều được xác minh bằng probe trên node thật trước khi sửa.

1. **Document ghi trước Commit 11 bị đóng băng vĩnh viễn.** Chúng không có field `revision`. Painless ném NullPointerException khi so `>=` với null (`"cannot compare"`, xác minh bằng `_update` trực tiếp), nên ES trả 400 → `EnsureValid` throw → consumer retry tới DLQ: **mọi event đã index bởi bản đang chạy trở nên không thể cập nhật ngay khi deploy**. MongoDB hỏng lặng lẽ hơn và vì thế tệ hơn: `$lte` là type-bracketed, field thiếu **không bao giờ** match một so sánh số, nên filter trượt → upsert cố insert → E11000 → đúng cái `catch` "message này thua" nuốt mất. Không log, không retry, lịch của mọi event cũ đứng im mãi mãi. Cả hai giờ đọc revision thiếu là "unknown" = `0` theo đúng quy tắc expand-only ở mục 10, tức thua mọi ghi thật thay vì chặn nó.
2. **`Math.max` biến revision thành số thực.** Painless resolve `Math.max` trên toán hạng `def` sang overload `double`, nên script cancel ghi `revision: 4.0`; `System.Text.Json` không đọc được `4.0` thành `long`, và **document trở thành không đọc nổi qua `GetByIdAsync` ngay khoảnh khắc nó bị huỷ**. Thay bằng `if` so sánh rồi gán — giữ nguyên kiểu số nguyên nó đến.
3. **Version conflict bị dán nhãn "ES sập".** `_update` là chu trình get-modify-index có seq_no gác, nên hai apply đồng thời lên một document làm tất cả trừ một fail — đo được **9/10** trên node thật. `retry_on_conflict` là câu trả lời đúng và an toàn ở đây vì mỗi lần thử lại **chạy lại script trên state vừa đọc**, tức quyết định lại chứ không phát lại một ghi mù. Chọn 10 có đo, không phải số tròn: với document đã tồn tại thì 5 đã đủ, nhưng với document đang được chính storm đó tạo ra, 5 vẫn còn **4/10** fail còn 10 thì sạch — đường upsert-create chen nhau nặng hơn vì mọi writer đều bắt đầu bằng việc thua kẻ tạo. Đây là *bound*, không phải guarantee: storm rộng hơn vẫn hiện ra thành một apply thất bại, consumer redeliver, và script quyết định lại đúng trên đường quay lại.

**Suite này chưa từng chạy được trọn vẹn trước hôm nay — ba lỗi hạ tầng test, ghi lại vì đều là bẫy chung.** (1) `Testcontainers.Elasticsearch` được xây quanh image mặc định của nó (8.6.1) với security **bật**, và chờ một dòng log mà 8.15.3 + security tắt không bao giờ in ra: `StartAsync` đơn giản là không bao giờ trả về — quan sát được là một lần chạy ngồi quá **15 phút** với cả năm container up, healthy và hoàn toàn nhàn rỗi. Đổi sang chờ một HTTP request thật được trả lời (`/_cluster/health`) đưa nguyên suite về **~37 giây**; đó cũng là điều kiện trung thực hơn — test cần một node *phục vụ được request*, không phải một node *đã in một câu nhất định*. (2) Cùng gốc: `GetConnectionString()` trả `https://elastic:...@host:port` vì module giả định security bật, nên client bắt tay TLS với một node nói HTTP thuần và fail bằng `"Cannot determine the frame size or a corrupted frame was received"` — một thông báo đọc như node hỏng chứ không như sai scheme. Phải tự dựng URI. (3) Fixture tạo `MongoClient` mà **không** gọi `MongoSerializationConfig.EnsureConfigured()` như composition root của Notification vẫn làm; driver v3 bỏ Guid representation ngầm nên mọi filter có Guid đều throw. Đúng loại divergence fixture-vs-production đã che hai bug thật ở Commit 8 và 9.

**Một flake của Commit 9 lộ ra nhờ suite lớn hơn, đã sửa đúng chỗ.** `A_manual_review_order_must_stop_arming_further_deadlines` xanh 3/3 khi chạy riêng, đỏ trong lần chạy cả suite — và **đỏ sau 1 giây**, nên không phải chuyện timeout như lần chẩn đoán ở Commit 10 (nới timeout lên 30s đã không chữa được gì). Nguyên nhân thật: test **lấy mẫu** `ProcessPayment` count ngay sau khi `ManualReviewAt` xuất hiện, nhưng `FireDeadlineAsync` trả về ngay khi strike count đã lưu nhúc nhích, còn lệnh gửi lại của chính strike đó mới được in-memory outbox nhả ra sau đó — mẫu đọc hụt lần gửi cuối rồi thấy nó đến giữa lúc assert. Số đúng vốn tất định (`1 + MaxDeadlineStrikes`, vì strike thứ 4 escalate thay vì gửi lại), nên thay lấy mẫu bằng **chờ đúng con số đó** — assertion mạnh lên chứ không nới ra. Cùng lỗi lấy mẫu ở `An_operator_resume_must_put_a_reviewed_order_back_under_automation` được sửa kèm.

**Gate đã chạy:** `Phase12Harness` xanh **126/126**, chạy lại lần hai vẫn **126/126** (103 → 126). Thêm 3 case Catalog trên **Postgres thật** (hai save đồng thời một event chỉ một người thắng, `xmin` biến kẻ thua thành conflict; 8 `AddSection` đồng thời cùng tên chỉ còn một section; tên chỉ khác hoa-thường vẫn bị functional index chặn), 6 case Search trên **Elasticsearch thật** và 5 case Notification trên **MongoDB thật** — gồm cả ba case rolling-upgrade và concurrency nêu trên. Cộng 6 permutation lifecycle **promote từ `ExpectedFailure` sang `Harness`** và 3 fact mới (stale Published không lùi lịch, v1 revision 0 không đè v2, event đã huỷ không xuất hiện trong kết quả tìm).

`Phase12ExpectedFailure` giờ **rỗng — 0 case còn lại**: `dotnet test --filter "Category=Phase12ExpectedFailure"` trả `No test matches`. Toàn bộ 23 blocker của baseline Commit 1 đã được promote, không cái nào bị `Skip` hay xoá. Điều đó cũng làm thoả điều kiện của rule 4 trong [`docs/phase-12-hardening-baseline.md`](phase-12-hardening-baseline.md) — nhưng việc đưa hardening project vào `TicketFlow.slnx`/CI vẫn để cho Commit 17, vì nó cần Docker trong CI và thuộc nhóm F. Regression `TicketFlow.slnx` xanh **86/86**.

### Commit 12 — `feat(identity,recommendation): durable registration + concurrency-safe user history` ✅ DONE
- Identity registration workflow `Pending/Completed/CompensationPending`, stable registration key, query Keycloak theo normalized username/email sau uncertain create; cleanup dùng independent bounded token và không mask original error.
- Keycloak user bắt đầu `emailVerified=false`, có verify required action; password policy + rate limit/abuse protection.
- Recommendation dùng `UserId` làm `_id` hoặc unique index; duplicate-key retry merge purchase đúng một lần.
- **Verify:** cancel request ở từng Keycloak step, lost create response, DB failure + cleanup failure, concurrent first purchases và email-squatting/weak-password cases.

**Kết quả thực tế (2026-07-23).**

**Registration giờ là workflow bền vững, không còn dual-write mù.** `UserRegistration` được commit `Pending` trước khi gọi Keycloak, mang stable `RegistrationKey`, checkpoint `CreateAttemptedAt`/`KeycloakUserId`, và kết thúc ở `Completed` hoặc `CompensationPending`. Password tuyệt đối không ghi DB; retry phải gửi lại password dưới cùng registration key. Unique normalized email chặn hai key tranh cùng email, còn `xmin` buộc request thua reload checkpoint rồi quyết định lại thay vì last-write-wins.

**Lost response không còn tạo user mồ côi hoặc chiếm nhầm account có sẵn.** User Keycloak được gắn attribute `ticketflowRegistrationKey`; sau một create bất định, Identity query exact normalized username/email nhưng chỉ nhận user có đúng ownership marker đó. Vì thế hai request cùng key có thể hội tụ vào một remote user, trong khi một account Keycloak tồn tại trước đó dù trùng email cũng không bị lấy làm account của registration mới.

**Compensation độc lập với request token.** Cancellation ở create/configure/local-commit vẫn dùng bounded recovery token riêng để query hoặc xoá user. Nếu cleanup hỏng, workflow ghi `CompensationPending`; lỗi cleanup được log nhưng exception gốc vẫn được rethrow với nguyên stack, không còn tình trạng cleanup failure che mất DB/Keycloak failure ban đầu. Migration `Phase12DurableUserRegistration` tạo bảng coordinator theo kiểu expand-only; design-time factory mới cũng ngăn EF tooling khởi động nhầm HTTP/observability khi sinh migration.

**Keycloak và abuse boundary.** User mới bắt đầu `emailVerified=false` với required action `VERIFY_EMAIL`. Realm bật verify-email, password policy 12 ký tự + upper/lower/digit/special + không chứa username, brute-force protection; API validate cùng policy trước side effect và endpoint `/register` có fixed-window rate limit trả 429.

**Recommendation race đã khép ở database boundary.** Unique `UserId` index vẫn là invariant tương thích với document cũ. Hai first-purchase upsert cùng lúc: winner insert, loser bắt duplicate-key rồi retry update không-upsert để `$addToSet` merge đúng một lần. `LastPurchasedAt` dùng `$max`, nên redelivery cũ cũng không kéo lịch sử lùi thời gian.

**Gate đã chạy:** 12/12 case Identity/Keycloak xanh (lost create response; replay cùng key; cancellation tại create/configure/local-commit; cleanup failure không mask lỗi DB; 6 weak-password case; exact-email recovery có ownership marker). Mongo/Postgres thật xanh cho 32 first purchases đồng thời, mỗi event redeliver hai lần nhưng chỉ còn một user document; hai registration key tranh cùng normalized email chỉ một winner. Toàn bộ `Phase12Harness` xanh **140/140** (126 → 140). Regression `TicketFlow.slnx` xanh **88/88**.

### Commit 13 — `feat(workflows): event cancellation + resumable onboarding + notification delivery outbox`
- Event cancellation tạo durable workflow/campaign ID, đóng sale trước rồi xử lý từng order theo state: expire/release hold, stop pending process, refund paid order, revoke issued ticket và notify customer. Mỗi order operation idempotent, retry không refund/release hai lần.
- Chuyển Venue → Event → SeatMap → Section/Price → Reservation inventory → Publish khỏi chuỗi server action client thành backend workflow có `CreationKey`, persisted step, resume và compensation/cleanup rõ.
- Mỗi step idempotent; retry form không tạo event/venue mới. UI đọc workflow state và hiển thị bước thất bại có thể resume.
- Notification tạo delivery attempt/outbox trước; provider idempotency key; chỉ mark delivered sau provider confirmation. **Production bắt buộc cấu hình delivery provider thật** và lưu provider message/reference ID; stub chỉ được phép trong `Development`/test, không được mark delivery production là thành công.
- **Verify:** cancel event có hold/pending/paid/ticketed orders rồi kill/retry ở từng bước; cuối cùng sale đóng, refund/release/revoke đúng một lần. Inject failure ở mọi onboarding step rồi retry/resume; reminder crash trước/sau send không mất hoặc gửi trùng về business outcome. Chạy contract/integration test với provider sandbox hoặc fake server mô phỏng API thật, xác nhận production configuration thiếu provider phải fail readiness/startup thay vì rơi về stub.

---

## Nhóm E — Security boundary + frontend correctness

### Commit 14 — `fix(authz): tenant/resource ownership, service auth, media limits` ✅ DONE
- Backfill/require `OrganizerId/TenantId` cho Event; scope mutation, analytics, poster, inventory và gate validation theo resource. Customer ownership áp dụng Ordering/Ticketing.
- Reservation/Ticketing/Catalog tự validate token; internal calls dùng service principal/audience riêng. Compose không expose service port rộng hơn nhu cầu; K8s thêm NetworkPolicy.
- Personalized recommendation lấy `sub` từ verified token; anonymous request chỉ dùng popular, không nhận arbitrary `userId`.
- Media upload có request-size, decoded pixel/dimension/decompression limits; ưu tiên streaming/direct object upload, validate content type bằng nội dung.
- **Verify:** authorization matrix anonymous/customer/organizer A/organizer B/admin/service-account trên mọi endpoint nhạy cảm; direct-service bypass và oversized image đều bị chặn.

**Kết quả thực tế (2026-07-24).**

**Role không phải quyền sở hữu.** `RequireOrganizer` chỉ trả lời "caller có phải *một* organizer không", chưa bao giờ trả lời "có phải organizer *của event này* không" — nên organizer B đổi lịch, publish, đổi giá, thậm chí **huỷ** event của organizer A chỉ cần biết id, mà huỷ là thao tác phá hoại nhất hệ thống có (đóng sale và vô hiệu mọi hold chưa confirm ngay lập tức, Commit 3). Giờ `Event.OrganizerId` — cột Commit 2 tạo sẵn expand-only và **chưa ai ghi** — được ghi từ `sub` lúc tạo, và mọi mutation đi qua `GetOwnedAsync`. Seat map thừa kế quyền sở hữu của event; workflow huỷ event kiểm tra owner **trước** cả nhánh "đã huỷ rồi" (trả lời "xong rồi" cho người lạ vẫn là tiết lộ). Onboarding workflow lưu owner **bền vững lúc Start**, không đọc lại từ người gọi resume — workflow resume được nghĩa là một organizer khác có thể nhận vơ một event đang dựng dở bằng cách resume nó.

**Null owner thuộc về *không ai*, không phải về *tất cả mọi người*.** Event tạo trước commit này có `OrganizerId = null`. Coi null là "ai sửa cũng được" thì trao toàn bộ event cũ cho organizer đầu tiên đoán trúng id; tự gán owner từ request thì vi phạm mục 10 ("không tự gán"). Nên null = organizer bị từ chối, admin vẫn vào được — fail-closed nhưng không biến dữ liệu cũ thành bất khả quản trị.

**Ba cửa hậu không hề có xác thực.** (1) `POST|DELETE /events/{id}/inventory` của Reservation **không có `RequireAuthorization` nào** — ai chạm được cổng đều provision hoặc **xoá sạch** inventory của một event đang bán. (2) gRPC `GetHoldQuote` cũng không xác thực, mà lại nhận `OwnerUserId` **từ request body** — bất kỳ ai tới được cổng gRPC đều đọc được hold quote của bất kỳ khách nào (ghế chính xác, số tiền, hạn giữ). (3) `GET /recommendations?userId=` của Recommendation: service này **không có `AddTicketFlowAuth`** và nhận userId từ query string, nên qua gateway thì bất kỳ khách đăng nhập nào cũng đọc được gợi ý suy ra từ lịch sử mua của người khác.

Cả ba giờ dùng `RequireServicePrincipal` (realm role `service`, client `ticketflow-internal` client-credentials, `ServiceTokenProvider` cache token theo `expires_in` và chia sẻ toàn process). Điểm cốt lõi: **role `service` không được cấp cho bất kỳ client người dùng nào, và admin cũng không lách được** — mấy route này nhận một danh tính đã-được-xác-minh làm *tham số*, nên tham số on-behalf-of chỉ an toàn khi caller **không thể là** người mà nó khai. Recommendation tách hẳn: route công khai lấy `sub`, không còn tham số userId để nhét gì vào, ẩn danh thì nhận popular; đường on-behalf-of cho aggregator của Catalog chuyển sang `/internal/recommendations/{userId}` sau service principal.

**Revenue và cổng soát vé không biết ai sở hữu event — nên Catalog nói cho họ biết.** `/revenue` trước đây bất kỳ organizer nào cũng đọc được doanh thu **toàn sàn**, chi tiết theo từng event, chỉ cần bỏ `eventId` đi. `/tickets/validate` thì bất kỳ organizer nào cũng check-in — tức **đốt** — vé của event không liên quan. Cả hai cần biết chủ sở hữu, mà Ticketing lẫn Analytics đều không có dữ liệu đó. Cách chọn: thêm `OrganizerId` (expand-only, cuối record) vào `CatalogEventPublished` và mỗi bên giữ một **projection event → organizer**, guard theo revision đúng như Search/Notification làm ở Commit 11. Hai lựa chọn còn lại đều tệ hơn: luồn `OrganizerId` qua quote → order → saga → vé/sale là sửa sáu service để học một sự thật Catalog vốn đã phát đi; còn gọi ngược về Catalog mỗi lần quét là đặt phụ thuộc đồng bộ lên **cửa quay** — Catalog chậm là hàng người xếp ở cửa. Analytics vì thế có consumer RabbitMQ đầu tiên; revenue lọc bằng semi-join vào `event_owners FINAL`.

**Ảnh không bom được nữa.** Magic-byte sniffing đã chứng minh bytes *là* PNG/JPEG thật, nhưng đó là câu hỏi khác với *giải nén ra tốn bao nhiêu*: một PNG ~450 KB khai 50000×50000 là ảnh hoàn toàn hợp lệ và bung ra ~10 GB RGBA khi `SKBitmap.Decode` chạm vào. `ImageLimits` đọc **chỉ header** qua `SKCodec` nên không bao giờ cấp phát chính cái buffer nó đang định từ chối, và phép nhân pixel để `long` **có chủ đích** — 32-bit sẽ tràn thành số âm và lọt đúng input mà nó sinh ra để chặn. Chặn ở `UploadPosterFunction` trước khi ghi S3 (nên bomb không bao giờ tới được thumbnail function), và **kiểm lại** trong `ThumbnailPosterFunction` vì input thật của nó là "thứ gì nằm dưới originals/", không phải "thứ upload function đã duyệt". Gateway thêm giới hạn kích thước với bounded read — `Content-Length` là fast-reject khi có và trung thực, còn request chunked thì không mang length nào cả.

**Điều chỉnh plan có bằng chứng:** (1) Cross-organizer trả **403 chứ không 404**. Ticketing (Commit 10) cố ý chọn 404 vì ở đó 404 giấu được việc order của người khác tồn tại; lập luận đó **không chuyển sang đây** — `GET /events` và `GET /events/{id}` vốn ẩn danh, sự tồn tại của event đã công khai, nên 404 chẳng giấu được gì mà chỉ đánh lạc hướng caller về lý do ghi thất bại. (2) Reservation inventory dùng `RequireServicePrincipal` chứ không `RequireOrganizer`: Reservation không có cách nào biết event thuộc organizer nào, nên "một organizer" hẹp đúng bằng "một người đăng nhập bất kỳ" — Catalog đã kiểm ownership trước khi gọi. (3) Bản Keycloak realm của K8s **thiếu nguyên khối password policy/brute-force của Commit 12** (chỉ compose có); đã đồng bộ trong commit này vì đó là drift theo hướng yếu đi của đúng thứ commit này lo.

**Nói thẳng về NetworkPolicy:** 10 policy render đúng, nhưng CNI mặc định của kind (kindnet) **không thi hành NetworkPolicy**, nên trên cluster CI chúng được tạo rồi bị bỏ qua. Chúng đúng với mọi CNI có thi hành (Calico/Cilium), và thứ thực sự giữ tuyến ở cả hai trường hợp là các check phía service trong cùng commit này — vốn là thứ tự phòng thủ đúng. **Đừng đọc một lần CI xanh như bằng chứng policy đã được thi hành.** Compose thì bỏ hẳn publish port của 10 service backend (chỉ còn gateway `:8080`); `docker-compose.debug-ports.yml` trả lại khi cần chọc một service lúc debug.

**Gate đã chạy:** `Phase12Harness` xanh **189/189** (174 → 189, thêm 15 case authorization). Regression `TicketFlow.slnx` xanh **88/88**. Ma trận mới gồm: event bị đóng dấu owner từ token; organizer B bị chặn ở reschedule/publish/price-tier; chính chủ và admin đều qua; event legacy null-owner từ chối organizer nhưng admin vẫn vào; publish có phát `OrganizerId`; cổng soát vé chỉ chủ sở hữu + admin, event chưa biết chủ thì **từ chối**, và message projection cũ không kéo ngược quyền sở hữu; bomb 12000×12000 và 9000×9000 (case tràn 32-bit) đều bị chặn từ header.

**Một flake cũ, chưa khép:** `A_manual_review_order_must_stop_arming_further_deadlines` (Commit 9, đã sửa một lần ở audit nhóm B) **đỏ 1 lần trong 5 lần chạy full-suite** ngay sau khi thêm 15 test mới, rồi xanh 4 lần liên tiếp sau đó và 4/4 khi chạy riêng. Không bắt được message vì các lần sau đều xanh. Không phải do commit này — nó không chạm gì tới đường deadline của saga — nhưng ghi lại để lần sau khỏi phải phát hiện lại: sửa ở audit nhóm B **giảm** chứ chưa **triệt** race đó.

### Commit 15 — `fix(web): checkout/live-seat/auth-session/timezone/polling contracts` ✅ DONE
- Customer web dùng checkout v2; handle `409` bằng discriminated result, disable double submit và giữ stable idempotency key qua retry/navigation.
- SeatPicker loại selected seat vừa invalid; reconnect SignalR phải `JoinEvent` lại + fetch snapshot, initial failure có bounded retry/fallback polling.
- Access/refresh token giữ server-only; single-flight refresh, refresh failure buộc re-auth; namespace toàn bộ OAuth cookie hoặc tách hostname dev.
- `datetime-local` gửi IANA zone/offset; store/render theo UTC + event zone. Order polling tuần tự, abortable, sequence-aware; fetch luôn check status + runtime validate payload.
- Catalog availability outage hiển thị `temporarily unavailable`, không giả `0/0 sold out`.
- BFF/HTTP client có timeout, cancellation và error mapping nhất quán; search UI dùng `total/page` thật thay vì im lặng chỉ hiển thị page đầu.
- Generate shared schema/client hoặc thêm runtime validator; bỏ handwritten type đã drift.
- **Verify:** Playwright tamper/double-submit, conflict, live invalidation/reconnect, expired refresh, timezone Asia/Saigon↔UTC, stale polling và degraded dependency.

**Kết quả thực tế (2026-07-26).**

**Checkout không còn tin bất kỳ giá trị có giá trị kinh tế nào từ URL/browser.** Trang checkout chỉ nhận `holdId` + stable UUID idempotency key, rồi đọc quote authoritative từ Reservation; request tạo order v2 chỉ có đúng `holdId`, `paymentMethod`, `idempotencyKey`. Server action trả discriminated `success/conflict/error`, `useActionState` khoá nút khi pending/success và retry giữ nguyên key. Playwright đã gắn thêm `totalAmount=0&currency=USD&seatIds=evil` nhưng UI vẫn hiện **500.000 VND / 1 seat**; `409` hiện conflict riêng; double-click chỉ tạo **1** request và body fixture không có seat/amount/currency do browser khai.

**Live seat được resync như một stream có snapshot, không còn là “SignalR chạy được thì may”.** Browser không nhận bearer token và route `/api/signalr-token` đã bỏ; gateway mở riêng **read-only public seat-status hub** (event/seat vốn là catalog công khai), còn mutation/availability BFF vẫn kiểm session. Initial connect retry có giới hạn rồi rơi sang polling tuần tự; reconnect luôn `JoinEvent` lại. Trong lúc lấy snapshot, client buffer update SignalR, thay bằng snapshot authoritative rồi replay update có `generation + occurredAt`; snapshot polling có revision quan sát cục bộ nên vẫn sửa được trạng thái khi một active hold chưa có allocation generation, nhưng polling không vô cớ huỷ selection nếu payload không đổi. Seat đã chuyển `Available → Held` bị loại khỏi form ngay và không tự được chọn lại khi về `Available`; Playwright đã kiểm cả vòng đổi trạng thái với **cùng/unknown upstream version**, rồi xác nhận một click mới chọn lại được. Reservation snapshot và durable notification đồng thời mang allocation generation/timestamp để bỏ duplicate/out-of-order delivery khi version tồn tại.

**Token boundary giờ đúng cả lúc Auth.js serialize session.** `@ticketflow/web-auth` gom refresh rotation single-flight có timeout, xoá cả access/refresh credential khi refresh thất bại và buộc UI re-auth. Cả **7 OAuth cookie** (session/callback/csrf/PKCE/state/nonce/WebAuthn challenge) được namespace theo app, không chỉ session cookie. Access token chỉ được gắn vào server-side session dùng bởi server component/action/BFF; wrapper của `/api/auth/session` strip access/refresh/server token nhưng giữ cookie headers. Playwright với Keycloak thật đọc endpoint này chỉ thấy user metadata + `userId`, không có bearer/refresh token.

**Timezone là dữ liệu domain, không phải format trang trí.** Form organizer biến `datetime-local` thành UTC instant, gửi thêm IANA zone và offset của từng instant; server kiểm offset khớp zone (kể cả DST), chuẩn hoá UTC rồi persist `TimeZoneId`. Migration expand-only thêm zone cho Event/onboarding workflow với default `UTC` cho dữ liệu cũ; publish/reschedule contract thêm field trailing nullable, Search projection/index giữ zone và customer/organizer render theo zone của event. Playwright tại timezone `Asia/Saigon` đã gửi `2026-08-15 19:30` thành `2026-08-15T12:30:00Z`, zone `Asia/Saigon`, offset `420`, và trang browse/detail render `19:30 GMT+7`.

**HTTP/read-model contract được gom và fail rõ.** `@ticketflow/http-client` áp timeout, external cancellation, status/error mapping và runtime validation; `@ticketflow/types` tách contract/runtime/time helper dùng chung cho ba app. Order projection có monotonic `Sequence`; poll là recursive timeout không overlap, abort khi unmount, chỉ nhận sequence **strictly newer** và không dừng vì stale terminal response. Search dùng `total/page/pageSize` thật; payload drift bị từ chối. Catalog phân biệt `Available + []` với `TemporarilyUnavailable`, propagate cancellation của caller, và UI nói rõ dependency đang lỗi thay vì giả `0/0 sold out`.

**Điều chỉnh có bằng chứng từ browser gate.** Keycloak 26.7 ban đầu không import được realm: mô tả `ticketflow-internal` vượt giới hạn 255 ký tự; sau khi sửa, bốn dev password plaintext cũ lại vi phạm chính password policy 12 ký tự/upper/lower/digit/special của realm. Hai bản compose/K8s đã được sửa đồng nhất, dev credential được rotate để thoả policy. Import trực tiếp `quay.io/keycloak/keycloak:26.7` xanh; login customer thật phát đúng `aud=ticketflow-api` và role `customer`. Đây không phải nới policy để giữ fixture cũ. Phần event/gate context và camera của scanner vẫn để nguyên cho Commit 16; commit này chỉ áp shared auth/HTTP/runtime boundary cho scanner.

**Gate đã chạy:** `pnpm run typecheck`, ESLint toàn workspace và production build của cả 3 Next.js app đều xanh; contract test web **11/11**. `Phase12Harness` xanh **194/194** (189 → 194, thêm 5 case timezone/offset/order sequence/availability degradation/public hub); regression `TicketFlow.slnx` xanh **88/88**. Playwright CLI đã chạy login Keycloak thật, session-token leak check, search pagination/timezone, tamper + conflict + double-submit, injected stale order sequence, live invalidation/polling resync, organizer Asia/Saigon→UTC và dependency degradation. Case flake cũ `A_manual_review_order_must_stop_arming_further_deadlines` tái xuất đúng **1** lần ở một full run (193/194), sau đó xanh **4/4** khi chạy riêng và full rerun xanh 194/194; Commit 15 không chạm deadline saga, nên giữ lại bằng chứng thay vì giấu lần chạy đỏ.

### Commit 16 — `fix(scanner): multi-zone routing, event/gate context & camera lifecycle` ✅ DONE
- API/assets/manifest dùng `/scanner` base path/scope/start URL nhất quán; customer shell rewrite đúng BFF scanner route.
- Scanner gửi expected event/gate context, backend đối chiếu assignment; không cho organizer bất kỳ scan ticket của event khác.
- Chặn camera decode reentrancy, stop track/close `ImageBitmap` khi unmount/tab switch; xử lý offline rõ — nếu chưa có service worker thì không quảng cáo offline validation.
- **Verify:** production build multi-zone qua customer shell, manifest install path, wrong-event QR, parallel scan và mount/unmount camera loop không leak.

**Kết quả thực tế (2026-07-26).**

**Multi-zone không còn bỏ rơi BFF/PWA ở root.** Customer shell map `/scanner/api/*` về route BFF gốc `/api/*` của scanner trước catch-all page rewrite; scanner standalone cũng rewrite public mount này về BFF của chính nó. Vì vậy client luôn gọi `/scanner/api/validate`, không có trường hợp qua shell rơi vào API không tồn tại của customer-web. Manifest cũ ở `public/` đã bỏ; route handler thực sự nằm tại `/scanner/manifest.webmanifest`, trả `start_url: /scanner`, `scope: /scanner/` và icon `/scanner-static/icon.svg`. Production shell + scanner đã chạy thật: manifest và icon đều `200`; POST BFF không session trả `401 Reauthentication required` (đúng BFF scanner, không phải `404` từ shell). Playwright mở route qua shell cũng đọc đúng manifest này.

**Một scan luôn mang event đã chọn và event là authorization boundary.** Trước khi bật camera/upload, scanner bắt buộc chọn UUID event và nhãn gate; BFF validate UUID rồi chuyển `eventId` sang Ticketing. Gate label chỉ là context hiển thị cho nhân viên: codebase hiện không có entity/nguồn dữ liệu assignment theo từng cửa, nên không giả vờ một chuỗi browser là policy. Assignment có thẩm quyền là projection `EventOwner` từ Commit 14; Ticketing đối chiếu organizer với event đó, rồi đối chiếu event của ticket trong cả fast path lẫn `UPDATE ... WHERE` atomic. Ticket legacy không có `EventId` giờ fail-closed `WrongEvent` cho tới khi reconcile, thay vì được vào bất kỳ event nào organizer sở hữu.

**Camera không còn có hai owner cùng lúc.** `startingCamera`, `decodeInFlight` và `submitInFlight` chặn double click/decode/POST reentrancy. Cleanup dừng toàn bộ media track, huỷ animation frame và tách `video.srcObject`; tab hidden cũng áp cùng cleanup. Nếu permission trả về sau unmount/tab switch, stream vừa nhận bị stop ngay. Upload đóng `ImageBitmap` trong `finally`, kể cả native detector/canvas lỗi. UI ghi rõ chỉ có online validation và báo offline trước khi POST; không có service worker hay tuyên bố offline validation.

**Gate đã chạy:** `pnpm run typecheck`, ESLint toàn workspace, web contract tests **11/11**, và production build cả 3 Next.js app đều xanh (scanner liệt kê `/scanner` + `/scanner/manifest.webmanifest`). Hardening gate `ConcurrentCheckInTests` xanh **5/5** với Postgres thật: wrong-event, ticket legacy không event, revoked ticket, 2 scanner và 100 scanner song song; đúng một request `Valid`. Regression `TicketFlow.slnx` xanh **88/88**. Browser production check qua customer shell xác nhận manifest/icon/BFF route như trên; camera hardware không được giả lập ở browser gate vì phiên scanner tại máy này không có organizer session, nhưng lifecycle ownership được đóng ở mọi unmount/tab-hidden/late-permission path và cần lặp lại trong E2E authenticated scanner của Commit 18.

---

## Nhóm F — Durability hạ tầng, CI và finalize

### Commit 17 — `feat(ops): persistent stateful infra, migrations, readiness & production config` ⭐
- Redis AOF/PVC + rebuild runbook; RabbitMQ PVC/quorum durable queues; Kafka persistent logs/offsets; Keycloak dùng Postgres bền vững. Pin image tag/digest, không `latest`.
- Kafka topics/partition/retention được provision idempotent bằng Helm Job/IaC trước khi Analytics Ready; startup client vẫn có bounded retry/backoff cho broker/topic chưa sẵn sàng và không crash vĩnh viễn vì one-shot race. Không để mỗi application replica tự tạo topic cạnh tranh.
- Database migration chuyển khỏi app startup thành job có lock/ordering; scale nhiều replica không race schema.
- Tách `/health/live` và `/health/ready`; readiness kiểm tra datastore/broker/provider cần thiết nhưng có timeout và không tạo cascading load.
- Environment `Production` riêng; external secrets/rotation, TLS, least-privilege broker credential, NetworkPolicy, resource request/limit. Dev realm seeded credential không đi vào production release.
- LocalStack/functions được deploy/emulate đầy đủ trong CI hoặc khai báo external dependency có health gate; không trỏ mơ hồ `host.docker.internal` trong clean cluster.
- **Verify:** cài trên cluster trắng chứng minh topic tồn tại với đúng partition/retention trước khi Analytics Ready; broker khởi động chậm vẫn hồi phục qua retry. Delete/recreate từng Redis/RabbitMQ/Kafka/Keycloak pod giữa purchase; sau restart reconcile hội tụ, không oversell/mất user/message. Helm render security check không còn default credential/HTTP production path.

### Commit 18 — `test(ci): main branch gates + frontend/E2E/chaos suite — finalize Phase 12`
- Sửa workflow trigger `master` → `main`; job frontend dùng frozen lockfile + lint + typecheck + unit + Playwright multi-zone.
- CI stages theo chi phí: fast unit/contract → integration concurrency → E2E purchase/scanner → nightly/explicit chaos pod restart. Failure artifact lưu logs, traces, DB invariant query và seed để reproduce.
- Full-stack E2E: login → publish saleable event → hold → idempotent order → payment → ticket → PDF → one-time check-in; thêm cancel/refund và dependency outage path.
- Chạy flash-sale lại với invariant mới: sold allocation, confirmed order, authorized payment, issued ticket và analytics phải đối chiếu cùng business IDs, không chỉ đếm tổng `≤ capacity`.
- Viết `docs/phase-12-hardening-evidence.md`, cập nhật architecture/ADR/runbook và scope reconcile. Chỉ đánh dấu Phase 12 hoàn tất khi DoD matrix dưới có bằng chứng/link run thật.
- **[Delta review 2026-07-20]** CI thêm check đồng bộ 2 bản `realm-export.json` (`infrastructure/keycloak/` và `infrastructure/k8s/ticketflow/files/` — bản copy có chủ đích từ Phase 11 vì Helm chart phải tự chứa): diff phải bằng 0 hoặc job fail. Drift realm giữa compose và K8s là lỗi cấu hình gần như không thể thấy bằng mắt cho tới khi auth vỡ ở đúng một môi trường.
- **Verify:** pipeline `main` xanh từ clean checkout; kill/retry/duplicate suite xanh lặp lại; working dashboards/alerts thấy stuck/unknown/reconcile backlog.

---

## Sơ đồ Phase 12 — source of truth và các relay

```mermaid
flowchart LR
    UI["Customer web<br/>holdId + idempotency key"] --> GW["Gateway/BFF"]
    GW --> ORD["Ordering process manager<br/>single writer + Marten outbox"]
    CAT["Catalog<br/>sale policy + price revision"] -->|"versioned snapshot"| RES
    RES["Reservation Postgres<br/>SeatAllocation + receipt<br/>SOURCE OF TRUTH"] <--> CACHE["Redis<br/>hold/availability cache<br/>owner + generation"]
    ORD -->|"ConfirmSeats command"| RES
    RES -->|"authoritative allocation"| ORD
    ORD -->|"ProcessPayment orderId"| PAY["PaymentAttempt<br/>provider idempotency + reconcile"]
    PAY --> ORD
    ORD -->|"IssueTickets orderId"| TIX["Ticket issuance job<br/>deterministic artifact"]
    TIX --> ORD
    ORD --> DONE["Canonical OrderCompleted"]
    DONE -->|"checkpointed relay"| RMQ["RabbitMQ notification"]
    DONE -->|"checkpointed relay"| KFK["Kafka analytics"]
    DONE -.-> REC["Reconciler / manual review / DLQ runbook"]
```

---

## Thứ tự triển khai bắt buộc và parallelism an toàn

1. **Gate 0 — Characterization:** Commit 1 trước mọi behavior change. Commit 2 chỉ expand schema/contracts.
2. **Gate 1 — Money/seat safety:** Commit 3 → 4 → 5 → 6. Có thể làm payment song song với Reservation sau Commit 2, nhưng **không cutover checkout** trước khi cả authoritative quote và idempotent payment đều sẵn sàng.
3. **Gate 2 — Workflow durability:** Commit 7 → 8 → 9 theo đúng thứ tự. Không thêm saga timeout trước khi command/compensation đã idempotent, nếu không timeout chỉ khuếch đại side effect.
4. **Gate 3 — Downstream/resource security:** Commit 10-14 có thể chia team theo service sau Gate 2; migration ownership/revision vẫn phải theo expand/backfill/enforce.
5. **Gate 4 — Clients + production:** Commit 15-17; frontend cutover chỉ sau API v2. Commit 18 là release gate, không phải commit “viết docs cho đẹp”.

### Quy tắc rollback
- Rollback binary không đồng nghĩa rollback migration phá dữ liệu. Phase 12 chỉ dùng additive migration cho tới bước contract cuối.
- Khi writer v2 có lỗi: tắt feature flag, giữ schema/receipt/revision; không chuyển lại sang tin amount/seat từ client.
- Khi reconciler phát hiện owner conflict: quarantine + alert/manual review; không tự “chọn bên thắng”.
- Payment `Unknown` và ticket issuance uncertain không compensation tự động cho tới khi provider/object state được reconcile.

---

## Ma trận traceability từ audit đến commit

| Nhóm lỗi đã review | Commit khép lỗi | Bằng chứng đóng bắt buộc |
|---|---|---|
| Hold/order khác seat; client đổi giá/currency; event không saleable | 1-4 | Tamper suite + authoritative hold quote/allocation |
| Reservation confirm Redis→DB→event; SignalR trên critical path | 1, 2, 4 | Kill từng failpoint, redelivery trả cùng receipt |
| Stale compensation/ABA; inventory DB→Redis; Redis mất dữ liệu | 1, 2, 4, 5, 17 | Owner/generation CAS + rebuild/restart suite |
| Double charge, ambiguous timeout, local-only refund | 1, 2, 6 | Counting provider + `Unknown` reconcile + provider refund |
| Order save→publish gap; duplicate checkout | 1, 2, 7, 15 | Crash/retry trả cùng `OrderId` và một process |
| Milestone reorder/duplicate; admin command race; stream không expected version | 1, 8 | Permutation suite trên RabbitMQ + Marten, stream monotonic |
| Saga early response, không timeout, Kafka chặn completion | 1, 7-9 | Participant/broker/Kafka outage hội tụ trong deadline |
| Ticket artifact→DB dual-write, IDOR, double scan, wrong event | 1, 2, 10, 16 | Issuance recovery + auth matrix + parallel scan |
| Catalog stale write; Search/Notification resurrect; ES failure bị nuốt | 1, 2, 11 | Revision/tombstone permutation + response failure tests |
| Event cancel không refund/release/revoke | 3, 9, 10, 13 | Event-wide cancellation workflow qua mọi order state |
| Keycloak→Postgres registration; Mongo first-upsert race | 1, 2, 12 | Uncertain-create cleanup/reconcile + concurrent upsert |
| Organizer onboarding và notification delivery dual-write | 1, 13 | Fail từng workflow step; production provider thật xác nhận idempotent delivery, không fallback stub |
| Tenant/resource auth, direct-service bypass, media DoS/privacy | 2, 10, 14 | Cross-role authorization matrix + size/pixel limits |
| Checkout/live seat/token refresh/timezone/polling/type drift | 3, 15 | Multi-browser Playwright + runtime contract validation |
| Scanner multi-zone/camera/event-gate context | 10, 14, 16 | Shell production build + gate auth + lifecycle test |
| Ephemeral Redis/Rabbit/Kafka/Keycloak; Kafka topic startup race; migration/readiness/secrets | 17 | Clean-cluster topic provisioning + broker-delay retry + pod-delete recovery + Helm security/readiness checks |
| CI sai nhánh, thiếu frontend/E2E/fault tests | 1, 18 | Clean `main` pipeline + chaos artifacts |
| **Delta review 2026-07-20** (review độc lập lần 2, xác minh code thật): SignalR unsell không notify; `MyOrderView` thiếu PDF keys; metric vé bán đếm trùng redelivery; retry policy toàn cục 3×2s không phân loại lỗi; `realm-export.json` 2 bản drift | 5, 8, 9, 18 | Test case gọi tên riêng trong permutation/E2E suite + CI diff check realm |
| **Delta review 2026-07-21** (đối chiếu synthesis 2 review độc lập + soát code sau Commit 3): public HTTP confirm còn tồn tại và không owner-scope; release không owner-scope; lifecycle `hold_quotes` → `seat_allocations` chưa định nghĩa sau deviation Commit 3; thiếu per-user hold cap/rate limit | 4, 5 | Expected-failure case endpoint-exposure; ràng buộc quote→order trong migration/receipt; hold-cap test |

---

## Ma trận kiểm chứng DoD cuối Phase 12

| Invariant / DoD | Kiểm chứng bắt buộc |
|---|---|
| Client không đổi ghế/giá/currency | Tamper tests hold A/order B, amount 0, unsupported currency, khác owner đều bị chặn trước payment |
| Không oversell và không release nhầm owner | Concurrent hold/confirm + stale compensation sau resale; query Postgres chứng minh tối đa một active allocation/seat |
| Charge/refund effectively once | 100 distinct messages cùng `OrderId`, provider counting fake = đúng 1 charge/refund; response-loss reconcile thành công |
| Order stream monotonic | Mọi permutation/duplicate milestone trên RabbitMQ + Marten thật; replay projection bằng canonical state |
| Saga không treo vô hạn | Participant silence/Kafka down/broker restart đều tới Confirmed/Cancelled/ManualReview trong deadline và có alert |
| Ticket an toàn | Partial render recovery, owner IDOR bị chặn, wrong-event/refunded ticket bị từ chối, 100 scan chỉ 1 `Valid` |
| Projection không resurrect | Published/Rescheduled/Cancelled permutation + replay; revision/tombstone mới nhất thắng |
| Notification production giao thật | Production không có provider phải fail readiness/startup; provider sandbox trả reference ID; retry cùng operation không gửi trùng |
| Resource authorization | Matrix anonymous/customer/org A/org B/admin/service-account trên Catalog/Reservation/Ordering/Ticketing/Analytics/Media |
| Kafka topic không còn one-shot race | Helm/IaC provision idempotent trên cluster trắng; broker/topic khởi động chậm thì Analytics retry và cuối cùng Ready |
| Stateful restart không mất correctness | Xóa pod Redis/RabbitMQ/Kafka/Keycloak giữa E2E; recovery/reconcile không mất message/user/allocation |
| CI bảo vệ đúng nhánh | Push/PR `main` chạy backend + frontend + integration + E2E; nightly chaos có artifact và alert |

### Invariant query cần lưu cùng test artifact
- Mỗi `SeatId` có tối đa một allocation active; Redis owner/generation khớp Postgres sau reconcile.
- Mỗi `OrderId` có tối đa một authorized payment, một completed issuance và một canonical completion.
- `Confirmed order count`, `authorized payment count`, `issued ticket count`, `sold allocation count` đối chiếu bằng cùng business IDs; khác biệt phải bằng 0 hoặc nằm trong `Unknown/ManualReview` có deadline.
- Không projection nào có revision thấp hơn Catalog canonical revision; cancelled tombstone không bị active document revision cũ ghi đè.

---

## Rủi ro tổng hợp (probe theo thứ tự)

1. **Marten + MassTransit transaction boundary:** probe ở Commit 1-2. Nếu không share được transaction bền vững, dùng local Ordering outbox/dispatcher; không hạ tiêu chuẩn xuống in-memory outbox.
2. **Migration dữ liệu cũ thiếu owner/revision:** không backfill đoán. Tạo report, quarantine/manual mapping hoặc vô hiệu record không chứng minh được ownership.
3. **Provider idempotency/lookup thật có thể khác fake gateway:** adapter phải expose capability; provider không hỗ trợ thì dùng merchant reference unique + reconciliation, không retry mù.
4. **Redis rebuild khi còn active holds:** cần snapshot/version và cutover lock ngắn hoặc dual-read compare; không `FLUSHDB` rồi load trực tiếp trên production traffic.
5. **Queue cũ chứa contract v1:** consumer compatibility + queue drain metric bắt buộc trước contract cleanup.
6. **Timeout làm tăng compensation storm:** chỉ bật deadline sau khi participant/compensation effectively-once và có rate limit/backoff.
7. **K8s PVC single-node chỉ chứng minh restart, không chứng minh HA:** DoD Phase 12 là durability/recovery local; multi-zone managed HA thuộc backlog production platform.

---

## Hướng mở rộng (backlog — không thuộc Phase 12)

- Multi-region/active-active seat allocation, global clock/order guarantees và disaster recovery giữa region.
- PCI scope đầy đủ, tokenized payment method vault, 3DS/SCA và webhook signature của provider thật.
- Formal model/property-based testing cho saga/allocation state machine ngoài test permutation hiện tại.
- Automated customer-support console cho `ManualReview`, chargeback/dispute và partial event cancellation.
- Full GitOps/ArgoCD, managed Kafka/Rabbit/Postgres HA, backup restore drill ngoài kind single-node.
- Offline cryptographic ticket validation; Phase 12 vẫn chọn server-authoritative online validation.
- Precompute/cache popular-events aggregation của Recommendation (`GetPopularEventIdsAsync` hiện unwind + group toàn bộ collection mỗi request) — performance, không phải correctness; không chặn Phase 12.

## Thứ tự đọc trước khi code

Đọc lại **Phase 2** (Redis hold), **Phase 3** (outbox/inbox), **Phase 4** (Marten event stream), **Phase 5** (PurchaseSaga), **Phase 7** (auth hai tầng), **Phase 8** (BFF/SignalR/scanner), **Phase 11** (K8s/CI). Bổ sung: MassTransit transactional outbox/saga persistence semantics đúng version đang pin; Marten optimistic concurrency/subscriptions; PostgreSQL `xmin`/conditional update/advisory lock; Redis Lua compare-and-set; payment-provider idempotency + ambiguous outcome reconciliation; OAuth BFF/token handling; Kubernetes StatefulSet/PVC/quorum queue; chaos/fault-injection testing.
