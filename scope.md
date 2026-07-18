---
title: "TicketFlow — Scope & Kế hoạch chi tiết"
project: TicketFlow
stack: .NET microservices + Next.js
goal: "Học trọn 17 chương 'Microservices Design Patterns in .NET' + toàn bộ tech stack của grab qua 1 project full .NET"
created: 2026-07-13
tags: [project, dotnet, microservices, ticketflow, scope]
---

# 🎟️ TicketFlow — Scope & Kế hoạch chi tiết

Nền tảng bán vé sự kiện (concert / thể thao) xây bằng **.NET microservices**. Mục tiêu học tập: đi hết **17 chương** sách *Microservices Design Patterns in .NET (Trevoir Williams, 2nd Ed.)* và **toàn bộ công nghệ** trong project `D:\code\grab`, nhưng trên một domain mới do mình tự thiết kế kiến trúc từ đầu.

> **Cách dùng file này:** đây là "bản đồ" tổng thể. Mỗi phase là một milestone chạy được. Không nhảy phase — mỗi phase dựa trên phase trước. Khi làm xong 1 phase, tick vào "Definition of Done" của nó.

---

## 1. Tại sao domain "bán vé"?

Domain này **tự nhiên** đòi hỏi mọi pattern trong sách, không phải nhét gượng:

- **Saga + concurrency**: nhiều người giành 1 ghế cùng lúc → chống overselling là bài toán distributed systems thật.
- **Event Sourcing**: vòng đời vé/đơn hàng (đặt → giữ chỗ → thanh toán → phát vé → check-in / hủy / hoàn tiền) là chuỗi event bất biến hoàn hảo.
- **CQRS**: đọc ("vé của tôi", "sơ đồ ghế còn trống") và ghi (đặt vé) có yêu cầu rất khác nhau.
- Là câu **system design interview kinh điển** ("design Ticketmaster") → học và luyện phỏng vấn cùng lúc.

---

## 2. Nguyên tắc thiết kế

1. **Database-per-service** — không service nào đụng DB của service khác. Giao tiếp qua API hoặc event.
2. **Domain-Driven Design** — mỗi service là một bounded context, có ngôn ngữ chung (ubiquitous language) riêng.
3. **Async-first** — mặc định giao tiếp bất đồng bộ qua message; chỉ dùng sync (gRPC/REST) khi thật sự cần trả lời ngay trong request.
4. **Event-driven + Outbox** — mọi thay đổi state phát ra domain event, publish tin cậy bằng **Transactional Outbox** (không mất event khi DB commit nhưng broker chết).
5. **Resilient by default** — mọi lời gọi mạng bọc Polly (retry + circuit breaker + timeout), mọi handler idempotent.
6. **Observable by default** — mọi service gắn OpenTelemetry (trace + metric + log) ngay từ đầu, không để tới cuối.

---

## 3. Kiến trúc tổng thể

```mermaid
flowchart TB
    subgraph Client["CLIENT LAYER — Next.js Multi-Zones (customer-web = shell)"]
        direction LR
        CW[customer-web]
        OD[organizer-dashboard]
        GS[gate-scanner PWA]
    end

    GW["GATEWAY LAYER — YARP<br/>API Gateway + BFF + Auth check"]

    subgraph Services["SERVICE LAYER — .NET"]
        direction LR
        ID[identity]
        CAT[catalog]
        RES[reservation]
        ORD["ordering<br/>(saga)"]
        PAY[payment]
        TIC[ticketing]
        SRCH[search]
        NOT[notification]
        REC[recommendation]
        ANA[analytics]
        MED[media]
    end

    subgraph Data["DATA LAYER"]
        direction LR
        PG[(PostgreSQL)]
        MG[(MongoDB)]
        RD[(Redis)]
        ES[(Elasticsearch)]
        KF[[Kafka]]
        RB[[RabbitMQ]]
        MN[(MinIO)]
        CH[(ClickHouse)]
    end

    OBS["Observability<br/>Jaeger · Prometheus/Grafana · ELK"]

    Client -->|HTTPS / GraphQL| GW
    GW -->|gRPC sync · Kafka/RabbitMQ async| Services
    Services --> Data
    Services -.OpenTelemetry.-> OBS
```

> Runtime: Docker Compose (local) → Kubernetes (Ch14).

---

## 4. Bounded Contexts / Services

| Service | Trách nhiệm | DB chính | Store phụ | Port (HTTP/gRPC) |
|---|---|---|---|---|
| **gateway** | Định tuyến, xác thực token (BFF dời Phase 8) | — | — | 8080 |
| **identity-service** | User directory/profile; authN đã chuyển Keycloak (Phase 7) | PostgreSQL | — | 8001 / 9001 |
| **catalog-service** | Sự kiện, venue, sơ đồ ghế, hạng giá | PostgreSQL | — | 8002 / 9002 |
| **reservation-service** | Giữ chỗ (hold có TTL), tình trạng ghế, chống overselling | Redis (lock/hold) | PostgreSQL (chốt) | 8003 / 9003 |
| **ordering-service** | Vòng đời đơn hàng, **host Saga** (event-sourced + CQRS) | PostgreSQL (Marten) | — | 8004 / 9004 |
| **payment-service** | Thanh toán, ví, hoàn tiền (resiliency) | PostgreSQL | — | 8007 (không gRPC — xem Phase 5 quyết định kiến trúc mục 2) |
| **ticketing-service** | Phát vé, sinh QR/PDF, validate tại cổng | PostgreSQL | MinIO (PDF) | 8008 (không gRPC — xem Phase 5 quyết định kiến trúc mục 2) |
| **search-service** | Tìm sự kiện theo nghệ sĩ/venue/ngày/thành phố | Elasticsearch | — | 8006 |
| **notification-service** | Email/SMS/push (vé, nhắc lịch) | MongoDB | RabbitMQ | 8005 |
| **recommendation-service** | "Sự kiện gợi ý cho bạn" (theo nghệ sĩ/venue/phổ biến) | MongoDB | — | 8009 |
| **analytics-service** | Doanh số, lượt xem, dashboard organizer | ClickHouse | — | 8010 |
| **media-service** | Poster, ảnh nghệ sĩ, thumbnail — **không phải service HTTP riêng** (không port :8011 như dự tính ban đầu). Là 2 AWS Lambda (`UploadPosterFunction`/`GetPosterFunction`/`ThumbnailPosterFunction`, chạy qua LocalStack, xem Phase 9) mà gateway gọi thẳng qua Lambda Invoke API — reconcile ở Phase 9 Commit 5 | LocalStack S3 | — | — |

Frontends (micro-frontends — Ch12):

| App | Vai trò | Tech | Port |
|---|---|---|---|
| customer-web | Duyệt, tìm, mua vé; **shell** của Multi-Zones (rewrite `/organizer/*`, `/scanner/*`) | Next.js 16.2.10 + Tailwind (shadcn không dùng — component tự viết đủ cho nhu cầu, xem Phase 8 mục 6) | 3000 |
| organizer-dashboard | Tạo sự kiện, sơ đồ ghế, xem doanh số | Next.js 16.2.10 | 3001 |
| gate-scanner | Quét QR check-in tại cổng | Next.js 16.2.10 PWA (manifest, không service worker) | 3002 |

---

## 5. Hạ tầng dùng chung & lý do chọn

| Công nghệ | Dùng cho | Chương liên quan |
|---|---|---|
| **PostgreSQL 16** | System of record cho catalog/identity/ordering/payment/ticketing | Ch8 |
| **MongoDB 7** | Read model & dữ liệu linh hoạt (notification, recommendation) | Ch8 |
| **Redis 7** | Distributed lock giữ ghế, cache, pub/sub real-time | Ch8, Ch10 |
| **Elasticsearch 8** | Full-text search sự kiện | Ch8 |
| **Kafka** | **Event bus** domain event + feed event-sourcing + nạp analytics (throughput cao, replay được) | Ch4, Ch7 |
| **RabbitMQ** | **Command/work queue** + transport cho MassTransit Saga (routing, ack, retry) | Ch4, Ch9 |
| **MinIO** | Lưu poster, PDF vé (S3-compatible) | Ch15 |
| **ClickHouse** | Kho analytics dạng cột, query tổng hợp nhanh | Ch16 |
| **Keycloak** | OAuth2/OIDC authority — realm/client/role reproducible từ JSON | Ch13 |
| **OpenTelemetry + Jaeger** | Distributed tracing | Ch16 |
| **Prometheus + Grafana** | Metrics + dashboard + alert | Ch16 |
| **ELK (Serilog→ES→Kibana)** | Log tập trung | Ch16 |
| **Docker + K8s** | Đóng gói & orchestration | Ch14 |
| **AWS Lambda + LocalStack** | Serverless (thumbnail poster, sinh PDF, cron nhắc lịch) — đổi từ **Azure Functions** (scope gốc) ở Phase 9 Commit 5: pattern FaaS/trigger/binding của Ch15 là khái niệm chung, không thuộc vendor; AWS phổ biến hơn trên thị trường việc làm, đổi lúc Phase 9 chưa có dòng code nào nên chi phí đổi = sửa plan. LocalStack chạy AWS thật (Community, không phải giả lập riêng) — "serverless" ở đây là mô phỏng local, không phải AWS thật (xem [phase-9-plan.md](docs/phase-9-plan.md) đầu file) | Ch15 |
| **GitHub Actions** | CI/CD | Ch14, Ch17 |

> **Vì sao cả Kafka lẫn RabbitMQ?** Đây chính là bài học Ch4: chọn đúng công cụ cho đúng kiểu giao tiếp.
> - **Kafka** = luồng *sự kiện* (integration event, event-sourcing log, analytics) — nhiều consumer, replay được, throughput cao.
> - **RabbitMQ** = luồng *lệnh* (gửi 1 job cho đúng 1 handler: "gửi email này", các bước command của Saga) — routing linh hoạt, ack/nack.
> MassTransit chạy Saga trên RabbitMQ và dùng **Kafka Rider** cho stream event.

---

## 6. Bản đồ 17 Chương → Feature TicketFlow

| Chương | Pattern / chủ đề | Áp vào đâu trong TicketFlow |
|---|---|---|
| **Ch1** Big picture | Phân rã service, tư duy microservices | Thiết kế toàn kiến trúc, dựng skeleton + Docker Compose |
| **Ch2** DDD | Bounded context, Aggregate, Value Object, Domain Event | Mô hình hóa Event/Order/Ticket/Seat; định ranh giới service |
| **Ch3** Synchronous comm | REST + gRPC | Gateway→service (REST); ordering→reservation xác nhận ghế (gRPC) |
| **Ch4** Asynchronous comm | Message bus, Outbox | Domain event qua Kafka; job qua RabbitMQ; Outbox chống mất event |
| **Ch5** Aggregator | Gộp nhiều nguồn thành 1 response | Trang chi tiết sự kiện = catalog + ghế còn trống + gợi ý (review drop khỏi Phase 6 — không có review-service/dữ liệu, xem Phase 6 quyết định kiến trúc mục 6) |
| **Ch6** CQRS | Tách read/write | ordering-service: write (đặt vé) vs read ("vé của tôi") |
| **Ch7** Event Sourcing | Event store, projection, replay | Order & Ticket lưu dạng event (Marten); build read model từ event |
| **Ch8** Database strategies | DB-per-service, polyglot persistence | Mỗi service DB riêng: Postgres/Mongo/Redis/ES/ClickHouse |
| **Ch9** **Saga** | Distributed transaction + compensation | **PurchaseSaga**: giữ ghế→thanh toán→phát vé→thông báo |
| **Ch10** Resiliency | Retry, Circuit Breaker, Timeout, Bulkhead | Payment bọc Polly; idempotency; graceful degradation |
| **Ch11** API & BFF Gateway | Gateway, BFF per client | YARP gateway + JWT validation per-route (Phase 7); BFF = tầng server-side của chính từng Next.js app (Phase 8 mục 2) — không dựng service BFF .NET riêng, double-BFF vô nghĩa khi Next.js đã làm sẵn việc đó |
| **Ch12** Micro-frontends | Composition ở frontend | 3 app Next.js (admin-panel backlog, mục 7), **Next.js Multi-Zones** (customer-web = shell) thay Module Federation — probe Commit 3 xác nhận package đó đã "maintenance mode", App Router không được hỗ trợ; shared `types` package (`ui` giữ rỗng, chưa có nhu cầu thật ≥2 app dùng chung component) |
| **Ch13** Security | AuthN/AuthZ, OAuth2/OIDC, JWT | Keycloak (authority); role-based 2 tầng — gateway validate token, service sở hữu rule tự enforce |
| **Ch14** Container hosting | Docker, Kubernetes, Helm | Dockerfile mỗi service; K8s manifest/Helm; deploy kind/minikube |
| **Ch15** Serverless | FaaS, event-triggered | Function: transcode poster, sinh PDF vé, cron nhắc lịch sự kiện |
| **Ch16** Observability | Trace/Metric/Log | OpenTelemetry toàn hệ; Jaeger + Prometheus/Grafana + ELK |
| **Ch17** Wrap-up | Ghép nối, review kiến trúc | Load test overselling, demo end-to-end, viết docs kiến trúc |

---

## 7. Luồng nghiệp vụ đinh — PurchaseSaga (mua vé)

Đây là trái tim của cả hệ thống, nơi Ch6/Ch7/Ch9/Ch10 hội tụ.

**Happy path:**

```mermaid
sequenceDiagram
    actor U as Khách
    participant R as reservation
    participant O as ordering (Saga)
    participant P as payment
    participant T as ticketing
    participant N as notification
    U->>R: Chọn ghế
    R->>R: HOLD ghế (Redis, TTL 10')
    U->>O: Bấm thanh toán
    O->>R: B1. ConfirmSeats (chốt hold)
    R-->>O: Đã chốt
    O->>P: B2. ProcessPayment (bọc Polly)
    P-->>O: Đã charge
    O->>T: B3. IssueTickets (QR + PDF → MinIO)
    T-->>O: Đã phát vé
    O->>N: B4. Notify (RabbitMQ)
    N-->>U: Email kèm vé
    Note over O: phát OrderCompleted → cập nhật read model
```

**Compensation (khi lỗi):**
| Lỗi ở bước | Hành động bù trừ |
|---|---|
| Payment fail | Release ghế → `OrderCancelled` |
| Ticketing fail | Refund payment → release ghế → `OrderCancelled` |
| Hết TTL trước khi thanh toán | Redis tự nhả hold → ghế trở lại "còn trống" |

**Event-sourced stream của 1 order:**

```mermaid
stateDiagram-v2
    [*] --> OrderStarted
    OrderStarted --> SeatsConfirmed
    SeatsConfirmed --> PaymentAuthorized
    PaymentAuthorized --> TicketsIssued
    TicketsIssued --> OrderCompleted
    OrderCompleted --> [*]

    SeatsConfirmed --> PaymentFailed: payment lỗi
    PaymentAuthorized --> TicketingFailed: ticketing lỗi
    TicketingFailed --> Refunded: hoàn tiền
    PaymentFailed --> SeatsReleased: nhả ghế
    Refunded --> SeatsReleased: nhả ghế
    SeatsReleased --> OrderCancelled
    OrderCancelled --> [*]
```

---

## 8. Cấu trúc monorepo

```
ticketflow/
├── src/
│   ├── BuildingBlocks/                # Thư viện dùng chung (KHÔNG phải shared DB)
│   │   ├── TicketFlow.Domain/         # Result, Entity, AggregateRoot, DomainEvent base
│   │   ├── TicketFlow.Messaging/      # Abstraction MassTransit, Outbox, contracts
│   │   ├── TicketFlow.Observability/  # Setup OpenTelemetry + Serilog dùng chung
│   │   └── TicketFlow.Web/            # Middleware, auth handler, exception filter
│   ├── Services/
│   │   ├── Identity/
│   │   ├── Catalog/
│   │   ├── Reservation/
│   │   ├── Ordering/                  # chứa Saga + event store
│   │   ├── Payment/
│   │   ├── Ticketing/
│   │   ├── Search/
│   │   ├── Notification/
│   │   ├── Recommendation/
│   │   └── Analytics/
│   ├── Gateway/                       # YARP + BFF
│   └── Functions/                     # AWS Lambda (serverless, chạy qua LocalStack — Phase 9,
│                                       # đổi từ "Azure Functions" scope gốc, xem mục 5)
├── contracts/                         # Event/message contracts (shared package)
├── apps/                              # Frontends Next.js (customer/organizer/scanner/admin)
├── packages/                          # ui, types, config dùng chung cho frontend
├── infrastructure/
│   ├── docker/                        # docker-compose.infra.yml, .monitoring.yml
│   ├── k8s/                           # manifests / Helm charts
│   └── monitoring/                    # prometheus, grafana, jaeger, elk config
├── .github/workflows/                 # CI/CD
├── TicketFlow.sln
└── scope.md                           # file này
```

Mỗi service theo **Clean/Vertical Slice**: `Api / Application / Domain / Infrastructure`.

---

## 9. Cross-cutting (làm 1 lần, dùng mọi nơi — trong BuildingBlocks)

- **MediatR** — dispatch command/query (nền cho CQRS).
- **Transactional Outbox** — publish event tin cậy.
- **OpenTelemetry** — trace/metric/log tự động.
- **Polly** — resilience policies dùng chung.
- **Result pattern** — không throw để điều khiển luồng nghiệp vụ.
- **Idempotency middleware** — chống xử lý trùng message.
- **Correlation ID** — xuyên suốt request qua mọi service.

---

## 10. KẾ HOẠCH STEP-BY-STEP THEO PHASE

> Mỗi phase kết thúc bằng một hệ **chạy được** và một **Definition of Done (DoD)**. Ước lượng công sức tính theo ~4h/ngày; cứ coi là linh hoạt.

### Phase 0 — Nền móng & tooling *(Ch1)* — ~1 tuần
**Mục tiêu:** dựng khung để bắt đầu, hiểu bức tranh lớn.
1. Tạo repo monorepo + `TicketFlow.sln` + cấu trúc thư mục ở mục 8.
2. Viết `docker-compose.infra.yml`: Postgres, Redis, RabbitMQ, Kafka (chỉ những cái cần trước).
3. Tạo `BuildingBlocks` skeleton (Domain, Web).
4. Dựng 1 service mẫu (`catalog`) trả về "hello" + health check + Swagger.
5. Dựng `gateway` (YARP) route tới catalog.
- **DoD:** `docker compose up` + gọi qua gateway → catalog trả 200; hiểu sơ đồ kiến trúc mục 3.

### Phase 1 — Domain modeling & CRUD services *(Ch2, Ch3, Ch8)* — ~2 tuần
**Mục tiêu:** DDD tactical patterns + giao tiếp sync + DB-per-service.
1. Mô hình `catalog`: Event, Venue, SeatMap, Section, PriceTier (Aggregate + Value Object).
2. `identity-service`: user/role, EF Core + Postgres (auth để Phase 7 hoàn thiện, giờ dùng token giả).
3. Định nghĩa domain event (chưa publish, mới raise nội bộ).
4. Thêm gRPC: `ordering` (skeleton) gọi `reservation` (skeleton) kiểm tra ghế — luyện sync comm.
5. Migration + seed data (vài sự kiện mẫu).
- **DoD:** ✅ CRUD sự kiện qua gateway; ✅ mỗi service DB riêng; ✅ 1 lời gọi gRPC nội bộ chạy được. **Hoàn thành 2026-07-13** — verify qua `docker compose up`, xem [docs/phase-1-plan.md](docs/phase-1-plan.md) để biết chi tiết từng commit và các lệch kế hoạch có lý do.

### Phase 2 — Reservation & bài toán overselling *(Ch8 + Redis)* — ~1.5 tuần
**Mục tiêu:** giải quyết concurrency — điểm nhấn distributed systems.
1. `reservation-service`: model tình trạng ghế (available/held/sold).
2. Giữ ghế bằng **Lua script nguyên tử trên Redis** (`SET NX EX` cho từng ghế trong 1 script — atomic all-or-nothing, không dùng RedLock vì đây không phải bài toán khóa tài nguyên chia sẻ giữa nhiều node mà là "giành quyền sở hữu" từng key, `SET NX` đã đủ) + key TTL 10 phút.
3. Chốt/nhả ghế; xử lý hết hạn hold tự động (TTL Redis tự dọn).
4. **Load test**: NBomber mô phỏng 1000 request giành 100 ghế qua HTTP thật → chứng minh không oversell; Testcontainers test giành 1000 request/1 ghế trong Redis thật → chứng minh đúng 1 người thắng.
- **DoD:** ✅ NBomber load test qua gateway, đúng số ghế được bán = số ghế cung cấp, phần còn lại nhận 409 "hết chỗ"; ✅ Testcontainers test 1000 concurrent hold/1 ghế → đúng 1 thắng. **Hoàn thành 2026-07-13** — verify qua `docker compose up` + smoke test 4 route gateway (catalog/identity/ordering/reservation đều 200), xem [docs/phase-2-plan.md](docs/phase-2-plan.md) để biết chi tiết từng commit và các lệch kế hoạch có lý do.

### Phase 3 — Async messaging & event-driven *(Ch4)* — ~1.5 tuần
**Mục tiêu:** chuyển từ gọi trực tiếp sang event.
1. Cài MassTransit (RabbitMQ) trong `BuildingBlocks.Messaging`. ~~Kafka Rider~~ **dời sang Phase 6** — Phase 3 chưa có consumer nào cần ngữ nghĩa stream/replay (chỉ pub/sub thông thường), thêm Kafka lúc này là scope creep; Kafka Rider sẽ bật khi `analytics-service` thật sự cần (Ch4 vẫn đọc để hiểu async messaging nói chung, không riêng RabbitMQ).
2. Triển khai **Transactional Outbox** (EF Core Outbox của MassTransit, gắn cùng `CatalogDbContext`).
3. `catalog` publish integration event `CatalogEventPublished` (đặt tên khác domain event `EventPublished` đã có sẵn trong Catalog Domain — tránh nhầm domain event nội bộ với contract cross-service); `search` (skeleton) consume để chuẩn bị index.
4. `notification-service` consume event → ghi log "sẽ gửi".
- **DoD:** ✅ tạo sự kiện ở catalog → event chảy qua bus → notification nhận được; ✅ tắt broker giữa chừng, bật lại → event không mất (Outbox). **Hoàn thành 2026-07-14** — verify qua `docker compose up` (11 container) + kịch bản tắt/bật RabbitMQ thật, xem [docs/phase-3-plan.md](docs/phase-3-plan.md) để biết chi tiết từng commit, bug thật gặp phải (va chạm tên queue giữa 2 service phá fan-out) và các lệch kế hoạch có lý do.

### Phase 4 — CQRS + Event Sourcing (Ordering) *(Ch6, Ch7)* — ~2 tuần
**Mục tiêu:** tách read/write, lưu lịch sử dạng event.
1. Cài **Marten** (event store trên Postgres, MIT license) cho `ordering`.
2. Order là aggregate event-sourced: `Apply` event để dựng lại state (`Order.Load`/Marten `AggregateStreamAsync`).
3. ~~MediatR~~ **tự cuộn dispatcher tối thiểu** (`ICommandHandler`/`IQueryHandler`, thuần convention đặt tên, không mediator runtime) — tách Command (`PlaceOrder`/`ConfirmOrder`/`CancelOrder`) và Query (`GetMyOrders`/`GetOrderById`). Lý do đổi: MediatR v13+ đã chuyển giấy phép thương mại (giống MassTransit v9); giá trị thật của nó (pipeline behaviors) không cần cho DoD Phase 4.
4. **Projection** (`MyOrdersProjection`, inline) → read model "đơn/vé của tôi" (`MyOrderView`), tách riêng port đọc (`IOrderViewStore`) khỏi port ghi (`IOrderStore`).
- **DoD:** ✅ đặt 1 order sinh ra stream event; ✅ xóa read model rồi **replay** từ event dựng lại được. **Hoàn thành 2026-07-14** — verify qua `docker compose up` (11 container) + kịch bản xoá/rebuild read model thật (toàn bộ 15 stream lịch sử khôi phục đúng), xem [docs/phase-4-plan.md](docs/phase-4-plan.md) để biết chi tiết từng commit và các bug thật gặp phải (nhiều lần liên quan JasperFx source generator: `partial` projection subclass, convention identity `Id`, và 1 bug rò rỉ dữ liệu chéo giữa aggregate với `Guid.Empty`).

### Phase 5 — PurchaseSaga + Payment + Ticketing + Resiliency *(Ch9, Ch10)* — ~2.5 tuần
**Mục tiêu:** luồng nghiệp vụ đinh + chịu lỗi.
1. `payment-service` (giả lập cổng thanh toán) + `ticketing-service` (QR + PDF → MinIO).
2. Viết **PurchaseSaga** (MassTransit state machine) đúng luồng mục 7.
3. Cài **Polly** cho payment: retry + circuit breaker + timeout.
4. Cài đặt **compensation** đầy đủ; đảm bảo mọi handler idempotent.
5. Test kịch bản lỗi: payment fail, ticketing fail, timeout hold.
- **DoD:** ✅ mua vé end-to-end thành công; ✅ ép lỗi từng bước → hệ tự bù trừ (nhả ghế/hoàn tiền) đúng. **Hoàn thành 2026-07-15** — verify qua `docker compose up` (14 container) + PurchaseSaga (MassTransit state machine, host trong `ordering`) điều phối 4 participant thật (reservation/payment/ticketing/notification) qua RabbitMQ, không mock. Happy path cuối cùng xác nhận toàn chuỗi thật: hold ghế → đặt hàng → `Order.Status = Confirmed`, ghế `Sold`, thanh toán `Succeeded`, 2 PDF vé thật trong MinIO (QR nhúng sẵn), notification log đúng tên/email khách. Cả 3 kịch bản lỗi DoD (payment fail, ticketing fail, hold không hợp lệ) verify **2 lần độc lập** (Commit 9 phát hiện + Commit 10 xác nhận sạch lại sau fix) — mỗi lần đều ghế nhả/tiền hoàn đúng qua cả Redis lẫn Postgres. Xem [docs/phase-5-plan.md](docs/phase-5-plan.md) để biết chi tiết từng commit — phase lớn nhất dự án, gặp nhiều bug thật đáng chú ý: `MassTransit.Marten` không có Outbox (khác EF Core), `ReleaseSeats` chỉ nhả được hold "Held" chứ không "huỷ bán" được ghế đã "Sold" (gap kiến trúc thật giữa Phase 2 và saga compensation), và 1 race condition Marten silent (không exception) khiến `MyOrderView.Status` bị milestone trung gian ghi đè lên status terminal.

### Phase 6 — Search, Aggregator, Recommendation, Analytics *(Ch5 + ES/ClickHouse)* — ~2 tuần
1. `search-service`: index sự kiện vào **Elasticsearch**, cập nhật theo event.
2. **Aggregator**: endpoint trang chi tiết gộp catalog + ghế trống + gợi ý (3 nguồn thật — review drop, không có review-service/dữ liệu). Đặt trong `catalog` (`GET /events/{id}/detail`), không tách aggregator-service riêng — YAGNI, catalog là chủ tự nhiên của trang chi tiết.
3. `recommendation-service`: gợi ý đơn giản theo **nghệ sĩ/venue/phổ biến** (không phải "thể loại" — `catalog.Event` không có field genre, gợi ý theo thể loại là vaporware) — chỉ Mongo, không cần Redis.
4. `analytics-service`: consume event doanh số/lượt xem → **ClickHouse**; query tổng hợp. **Bật Kafka Rider ở đây** (dời từ Phase 3 — xem ghi chú Phase 3) trong `BuildingBlocks.Messaging`: đây là consumer đầu tiên thật sự cần ngữ nghĩa stream/replay (event log doanh số thông lượng cao), khác RabbitMQ pub/sub thông thường đã dùng từ Phase 3.
- **DoD:** ✅ tìm sự kiện qua ES; ✅ trang chi tiết 1 request trả dữ liệu gộp; ✅ dashboard doanh số query ClickHouse. **Hoàn thành 2026-07-15** — verify qua `docker compose ps` (19 container) + luồng mua vé thật 100% qua gateway (venue→event→seatmap→publish→inventory→hold→order, không chạm port trực tiếp bước nào), xác nhận cả 3 DoD phản ánh đúng ngay sau: sự kiện mới lên `GET /search/events`, doanh số lên `GET /analytics/revenue`, lịch sử mua lên `GET /recommendation/recommendations`. Xem [docs/phase-6-plan.md](docs/phase-6-plan.md) để biết chi tiết từng commit — 2 bug thật đáng chú ý: **Ordering treo vô thời hạn lúc start** (Commit 6) do saga's constructor phụ thuộc trực tiếp `ITopicProducer` của Kafka Rider gắn cùng bus → deadlock DI vòng tròn khi MassTransit khởi tạo (fix: resolve publisher lúc consume message, không phải lúc construct saga) — debug qua 5 vòng bisect bằng probe container thật; và ClickHouse `sum()` trên cột `UInt32` trả về `UInt64` chứ không phải `Int64`, driver cast cứng throw ngay (Commit 7). Cũng phát hiện gap thiết kế nhỏ ở Recommendation (popular-fallback không loại trừ sự kiện vừa mua) — ghi backlog, không sửa vì đúng tinh thần "đơn giản, không ML" đã chốt ở Commit 4.

### Phase 7 — Gateway & Security *(Ch11, Ch13)* — ~2 tuần
1. Hoàn thiện **YARP gateway**: JWT validation + authorization policy per-route.
2. Dựng **Keycloak** — OAuth2/OIDC, realm reproducible từ JSON.
3. Phân quyền role (customer/organizer/admin) 2 tầng: gateway validate rộng, service sở hữu rule tự enforce.
4. `ordering` đổi danh tính từ JWT claims thay vì tin request body — đóng gap Phase 5.
- **DoD:** login qua Keycloak → gọi API có token; endpoint organizer chặn user thường.
- **Hoàn thành 2026-07-16** — verify qua `docker compose ps` (20 container) + mua vé end-to-end 100% qua gateway bằng token thật (`customerId` trong order khớp chính xác claim `sub`, không lấy từ body). Xem [docs/phase-7-plan.md](docs/phase-7-plan.md) để biết chi tiết từng commit. 3 quyết định lệch scope ban đầu, đều ghi rõ lý do trong plan doc: **Keycloak thay vì "Keycloak hoặc Duende IdentityServer"** (Duende là bẫy license thương mại, cùng loại rủi ro đã né với MassTransit 9.x/MediatR); **BFF per-frontend dời sang Phase 8** (4 frontend chưa tồn tại, dựng BFF bây giờ là composition layer cho nhu cầu chưa có thật — cùng loại vaporware đã cắt ở Phase 6); **GraphQL federation drop hẳn** (scope tự đánh dấu "tùy chọn", không có client nào tiêu thụ). Nhiều bug thật phát hiện qua probe Keycloak Admin API/realm-export.json trước khi code (không đoán docs) — đáng chú ý nhất: access token của Keycloak 26.7 **mặc định không có claim `sub`** (xác nhận bằng cách test cả client hệ thống `admin-cli`, không phải lỗi cấu hình riêng), phải tự thêm protocol mapper.

### Phase 8 — Micro-frontends *(Ch12)* — ~2.5 tuần
1. `customer-web` (Next.js): tìm → chọn ghế → mua → xem vé.
2. `organizer-dashboard`: tạo sự kiện, thiết kế sơ đồ ghế, xem analytics.
3. `gate-scanner` (PWA): quét QR check-in.
4. `admin-panel`. Shared `ui`/`types` package; sơ đồ ghế real-time (SignalR).
- **DoD:** ✅ hành trình mua vé hoàn chỉnh trên UI thật; ✅ scanner validate được vé đã mua. **Hoàn thành 2026-07-17** — verify qua `docker compose ps` (20 container) + toàn bộ hành trình mua vé thật trên browser 100% (không chạm curl bước nào trong chính luồng mua: browse → login PKCE thật qua Keycloak → chọn ghế (cập nhật real-time qua SignalR giữa 2 tab) → hold → đặt đơn → `Confirmed` → tải PDF vé thật) và scanner validate thật (upload ảnh QR thật rasterize từ PDF thật → `Valid` → quét lại → `AlreadyCheckedIn` → QR bịa → `NotFound`). Xem [docs/phase-8-plan.md](docs/phase-8-plan.md) để biết chi tiết từng commit (9 commit, lần đầu dự án có code ngoài .NET). 3 quyết định lệch scope ban đầu, đều ghi rõ lý do trong plan doc: **`admin-panel` cắt hẳn** (backlog — surface backend thật cho admin chỉ có ~3 endpoint, chưa đủ 1 sản phẩm, dựng app thứ 4 cho ngần đó là vaporware, lần cắt thứ 4 của dự án); **Module Federation → Next.js Multi-Zones** (probe xác nhận package Module Federation cho Next.js đã "maintenance mode", App Router không có commitment phát triển — Multi-Zones là cách composition chính chủ duy nhất còn sống cho App Router); **BFF per-frontend = tầng server-side của chính từng Next.js app**, không dựng 4 service .NET trung gian (double-BFF vô nghĩa khi Next.js đã làm sẵn việc đó). Nhiều bug thật phát hiện qua probe/verify trực tiếp trên browser thật (không đoán): `BarcodeDetector` API xác nhận không có sẵn trên desktop (đúng dự đoán, `jsQR` là đường chạy chính chứ không chỉ fallback); session cookie đè lẫn nhau giữa 3 app do cookie trình duyệt chỉ scope theo hostname chứ không theo port (RFC 6265) — fix bằng tên cookie + secret riêng mỗi app; và Next.js `basePath` xung đột với cơ chế parse request nội bộ của NextAuth khi đi qua Multi-Zones rewrite (đổi sang `assetPrefix` + tổ chức route thủ công theo đúng pattern chính chủ của Next.js).

### Phase 9 — Serverless *(Ch15)* — ~1 tuần
1. Function transcode/thumbnail poster khi upload (event-triggered).
2. Function render PDF vé (tách khỏi ticketing service).
3. Function theo lịch: nhắc "sự kiện của bạn diễn ra sau 24h".
- **DoD:** ✅ upload poster → tự sinh thumbnail; ✅ cron nhắc lịch bắn notification. **Hoàn thành 2026-07-18** — verify qua `docker compose ps` (21 container, thêm `ticketflow-localstack`) + luồng thật không đoán: upload ảnh PNG 800×600 thật qua gateway → S3 notification thật trigger thumbnail Lambda → `200×150` đúng tỉ lệ; mua vé thật → PDF vé thật do Lambda sinh (không phải ticketing-service), QR nhúng đọc được; event trong cửa sổ 24h + mua vé thật → tick EventBridge thật → log nhắc đúng tên/email/sự kiện, tick sau không nhắc lại (idempotent, xác nhận bằng Mongo timestamp không đổi chứ không chỉ "không thấy log trùng"); event ngoài cửa sổ không bị nhắc, reschedule vào cửa sổ thì nhắc đúng. Xem [docs/phase-9-plan.md](docs/phase-9-plan.md) để biết chi tiết từng commit (5 commit). 1 quyết định lệch scope ban đầu, ghi rõ lý do trong plan doc: **Azure Functions → AWS Lambda + LocalStack** (đổi vì mục tiêu nghề nghiệp — AWS phổ biến hơn trên thị trường việc làm, pattern FaaS của Ch15 là khái niệm chung không thuộc vendor — đổi lúc Phase 9 chưa có dòng code nào nên chi phí đổi gần như 0; xem đầu [phase-9-plan.md](docs/phase-9-plan.md)). `media-service` (mục 4) hoá ra không phải service HTTP riêng như bảng gốc — là Lambda gateway gọi thẳng qua Invoke API, không port :8011 (cũng đã sửa ở mục 4). Bug thật đáng chú ý nhất: handler Lambda `void` làm crash Runtime Interface Emulator (Go-level nil pointer panic), gây vòng lặp trông giống retry storm — cô lập bằng cách loại bỏ từng biến số (xoá EventBridge rule, invoke tay, đọc log container thật), fix = mọi handler phải trả về giá trị, không dùng `void`, ghi thành quy ước bắt buộc cho cả dự án. LocalStack Function URL cũng phát hiện không dùng được cho gateway route (hostname random, không sống sót qua restart) — pivot sang Lambda Invoke API theo tên function, ổn định hơn hẳn.

### Phase 10 — Observability *(Ch16)* — ~1.5 tuần
1. Chuẩn hóa OpenTelemetry toàn service → **Jaeger** (trace).
2. Metrics → **Prometheus**, dashboard **Grafana** (RED metrics + business metrics như "vé bán/phút").
3. Log tập trung: Serilog → **Elasticsearch** → **Kibana**.
4. Alert rule (ví dụ: circuit breaker mở, tỉ lệ lỗi payment cao).
- **DoD:** 1 lần mua vé → trace xuyên suốt mọi service trên Jaeger; dashboard + alert hoạt động.

### Phase 11 — Container hosting, CI/CD & Wrap-up *(Ch14, Ch17)* — ~2 tuần
1. Dockerfile tối ưu (multi-stage) mọi service.
2. **Kubernetes** manifest/Helm; deploy lên kind/minikube (hoặc AKS).
3. **GitHub Actions**: build → test → push image → deploy.
4. Load test tổng thể kịch bản "mở bán vé hot" (flash sale).
5. Viết tài liệu kiến trúc + ADR (architecture decision records) + review lại toàn hệ.
- **DoD:** `helm install` lên cluster chạy full stack; pipeline xanh; có docs kiến trúc.

---

## 11. Chiến lược testing

- **Unit test**: domain logic (aggregate, saga state transitions).
- **Integration test**: Testcontainers (Postgres/Redis/RabbitMQ thật trong container).
- **Contract test**: message/event contracts giữa các service.
- **Load test**: k6 / NBomber — trọng tâm là overselling (Phase 2) và flash sale (Phase 11).
- **E2E**: Playwright cho hành trình mua vé trên frontend.

---

## 12. Rủi ro & lưu ý

- **Scope lớn** → bám nguyên tắc "mỗi phase phải chạy được", đừng làm song song nhiều phase.
- **Đừng vàng hóa (gold-plating)**: payment/ML gợi ý chỉ cần *đủ để học pattern*, không cần production-grade.
- **Observability sớm**: gắn OpenTelemetry ngay từ Phase 3, Phase 10 chỉ là hoàn thiện — nếu để tới cuối sẽ rất khó debug các phase trước.
- **Marten vs EventStoreDB**: chọn **Marten** (chạy trên Postgres sẵn có) để bớt 1 hạ tầng; ghi chú EventStoreDB là lựa chọn thay thế nếu muốn tìm hiểu event store chuyên dụng.

---

## 13. Thứ tự đọc sách ↔ code

Đọc chương **ngay trước** khi bắt tay phase tương ứng (theo cột "Chương" ở mục 6 và mục 10). Cách hiệu quả nhất: đọc chương → code phase → quay lại đối chiếu chỗ mình làm khác sách và tự hỏi "tại sao".

| Tuần (tương đối) | Phase | Chương đọc trước |
|---|---|---|
| 1 | Phase 0 | Ch1 |
| 2–3 | Phase 1 | Ch2, Ch3, Ch8 |
| 4 | Phase 2 | Ch8 |
| 5 | Phase 3 | Ch4 |
| 6–7 | Phase 4 | Ch6, Ch7 |
| 8–9 | Phase 5 | Ch9, Ch10 |
| 10–11 | Phase 6 | Ch5 |
| 12–13 | Phase 7 | Ch11, Ch13 |
| 14–15 | Phase 8 | Ch12 |
| 16 | Phase 9 | Ch15 |
| 17 | Phase 10 | Ch16 |
| 18–19 | Phase 11 | Ch14, Ch17 |

> Tổng ~4–5 tháng ở nhịp 4h/ngày. Có thể cắt ngắn: **Phase 0→5 là "xương sống"** (đủ demo được luồng mua vé + Saga + event sourcing) — nếu cần dừng sớm để chuyển hướng, dừng sau Phase 5 vẫn là một portfolio mạnh.
