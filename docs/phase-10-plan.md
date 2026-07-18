---
title: "Phase 10 — Observability — Kế hoạch commit"
phase: 10
chapters: [Ch16 Observability]
created: 2026-07-18
tags: [ticketflow, phase-10, plan, observability, opentelemetry, jaeger, prometheus, grafana, serilog, elasticsearch, kibana, alerting]
---

# Phase 10 — Kế hoạch chi tiết theo commit

> **Mục tiêu (từ scope mục 10):** (1) OpenTelemetry toàn service → **Jaeger** (trace), (2) metrics → **Prometheus** + dashboard **Grafana** (RED metrics + business metrics như "vé bán/phút"), (3) log tập trung **Serilog → Elasticsearch → Kibana**, (4) alert rule (circuit breaker mở, tỉ lệ lỗi payment cao). Đây là nơi **Ch16 (Observability)** sống thật.
> **DoD:** 1 lần mua vé → trace xuyên suốt mọi service trên Jaeger; dashboard + alert hoạt động.

> ⚠️ **Viết TRƯỚC khi code** (giống phase-2…phase-9). Hai sự thật cần nói thẳng ngay từ đầu:
> 1. **Scope mục 12 ghi "Observability sớm: gắn OpenTelemetry ngay từ Phase 3, Phase 10 chỉ là hoàn thiện" — điều này KHÔNG xảy ra.** Khảo sát code thật trước khi viết plan này: không có project `TicketFlow.Observability` (scope mục 8 vẽ nó nhưng chưa từng tạo), không một package OpenTelemetry/Serilog nào trong `Directory.Packages.props`, grep toàn `src/` cho `OpenTelemetry|Serilog|ActivitySource|Meter` ra **0 kết quả**. Mọi service log bằng `ILogger` console mặc định. Phase 10 là **làm từ đầu**, không phải "hoàn thiện" — ghi nhận trung thực thay vì giả vờ nguyên tắc đã được tuân thủ. Điểm cộng duy nhất có sẵn: ASP.NET Core/HttpClient vốn tự tạo `Activity` (chưa export đi đâu), và MassTransit 8.x có built-in `ActivitySource "MassTransit"` (chưa ai subscribe). Reconcile vào scope.md ở Commit 5.
> 2. **DoD "trace xuyên suốt mọi service" là bài toán context propagation, không phải bài toán cài package.** Luồng mua vé đi qua ít nhất 4 loại hop khác bản chất: HTTP sync (browser→gateway→YARP→service), gRPC (ordering→reservation), **async qua RabbitMQ/MassTransit** (toàn bộ saga — hop quyết định trace liền hay đứt), và **Lambda Invoke API** (ticketing→RenderTicketPdf). Mỗi loại propagate `traceparent` theo cách khác nhau (hoặc không) — phần khó của phase này nằm hết ở đây, và mỗi loại phải **probe thật trên chính stack này** trước khi tin docs.

---

## Bối cảnh: cái gì đã có, cái gì còn thiếu

- **Đã có (Phase 1-9), Phase 10 móc vào:**
  - `TicketFlow.Web.AddTicketFlowWeb()/UseTicketFlowWeb()` — hook point dùng chung của cả 10 API service: health check `/health`, `ExceptionHandlingMiddleware` (đang trả `traceId = context.TraceIdentifier` — là connection id của Kestrel dạng `0HN...`, KHÔNG phải W3C trace id; Phase 10 đổi sang `Activity.Current.TraceId` để id user thấy khớp Jaeger).
  - `TicketFlow.Messaging.AddTicketFlowMessaging()` — hook point bus của mọi service; MassTransit tự đăng ký health check khi `AddMassTransit`.
  - Elasticsearch 8.15.3 sẵn trong compose (search-service dùng) — tái dùng làm log store, **chưa có Kibana**.
  - 21 container hiện tại: 10 infra (`docker-compose.infra.yml`) + 11 .NET (10 API + gateway, `docker-compose.yml`).
  - Polly (Phase 5) bọc payment — nguồn metric/alert "circuit breaker mở" mà scope đòi.
- **Còn thiếu — gap chặn DoD (khảo sát code thật, không đoán):**
  - **Toàn bộ.** Không OTel, không Serilog, không Jaeger/Prometheus/Grafana/Kibana container, không `docker-compose.monitoring.yml`, không thư mục `infrastructure/monitoring/`.
  - **Gateway không gọi `AddTicketFlowWeb`** — không có cả `/health` lẫn exception middleware. Gateway là hop ĐẦU TIÊN của mọi trace mua vé; không instrument nó là trace mất gốc. Phase 10 phải sửa gap này.
  - Lambda handlers log bằng `ILambdaContext.Logger` (string nội suy, vào CloudWatch Logs của LocalStack) — không structured, không trace context.

---

## Quyết định kiến trúc cho Phase 10

### 1. Tạo `TicketFlow.Observability` (building block scope hứa từ đầu) — một extension `AddTicketFlowObservability(serviceName)` cho cả 12 process
- Gói toàn bộ setup OTel (trace + metric) + Serilog vào 1 project, mỗi service gọi đúng 1 dòng — cùng triết lý `AddTicketFlowWeb`/`AddTicketFlowMessaging`. 12 chỗ gắn: 10 API + gateway + (một phần) Functions. Không copy-paste 12 lần cấu hình exporter.
- Config qua appsettings (`Otlp:Endpoint`, docker vs localhost) — cùng convention `Aws:LambdaServiceUrl` Phase 9.

### 2. Trace: OTLP thẳng vào **Jaeger all-in-one** — KHÔNG dựng OpenTelemetry Collector riêng
- Jaeger all-in-one nhận OTLP native (gRPC 4317/HTTP 4318) từ lâu — thêm 1 container collector trung gian là thêm 1 hop + 1 config file cho zero giá trị ở local single-node. Backlog khi nào cần fan-out (trace → nhiều backend) hay tail-sampling thật.
- **Rủi ro số 1 (quyết định DoD): MassTransit 8.5.10 có propagate `traceparent` qua RabbitMQ message header và emit `ActivitySource "MassTransit"` đúng như docs nói không** — probe đầu tiên của phase, trước mọi code khác: publish 1 message có Activity đang mở → consumer đọc header + xem Activity parent. Nếu đúng: trace saga liền mạch chỉ cần `.AddSource("MassTransit")`. Nếu sai: phải tự viết publish/consume filter — biết sớm để plan lại.
> **[Cập nhật sau Commit 1]** Probe thật xác nhận **đúng, và tốt hơn cả kỳ vọng**: propagate hoàn toàn tự động qua header `MT-Activity-Id` (định dạng `traceparent`), không cần code thêm — chỉ `.AddSource("MassTransit")` là đủ cho Commit 2. Chi tiết đầy đủ + bonus finding (RabbitMQ.Client 7.x cũng tự phát `ActivitySource` riêng) ở phần verify Commit 1.
- **Rủi ro số 2: Kafka Rider (SaleRecorded → analytics) có propagate như RabbitMQ transport không** — cùng DiagnosticSource về lý thuyết, nhưng Rider là code path riêng; probe riêng, không suy diễn từ kết quả RabbitMQ.
- **Hop Lambda (ticketing→RenderTicketPdf):** AWSSDK dùng HttpClient bên dưới → `OpenTelemetry.Instrumentation.Http` tự bắt client span của lời Invoke (đủ để trace mua vé "xuyên suốt" tới tận biên Lambda — thấy được span invoke + duration + lỗi). **Instrument BÊN TRONG Lambda (span con nối tiếp trong chính function) = backlog** — cần nhét `traceparent` vào payload + OTel SDK export từ trong Lambda container ra Jaeger; làm được (executor join `ticketflow-network` sẵn) nhưng không thuộc DoD, không kéo vào phase.
- SignalR (reservation→browser): server-side span có sẵn qua ASP.NET Core instrumentation; browser-side = backlog (không có code frontend instrumentation trong scope Ch16).

### 3. Metrics: **Prometheus scrape** endpoint `/metrics` từng service (OpenTelemetry.Exporter.Prometheus.AspNetCore) — không đẩy qua OTLP
- Pull model là cách Prometheus chính thống + đơn giản nhất khi không có collector; mỗi service expose `/metrics`, `prometheus.yml` liệt kê 11 target tĩnh (Docker DNS theo tên container — ổn định, không cần service discovery).
- RED metrics: `http.server.request.duration` (ASP.NET Core instrumentation cho sẵn rate/errors/duration theo route). Business metric **"vé bán/phút"**: custom `Meter` trong Ordering — counter `ticketflow.tickets.sold` tăng ở `OrderCompleted` (nơi số ghế đã chắc chắn bán xong), Grafana `rate()` ra per-minute. Đặt ở Ordering vì nó là chủ vòng đời đơn — không đặt ở Analytics (ClickHouse đã là 1 read model doanh số rồi, metric này là tín hiệu vận hành, khác mục đích).
- **Rủi ro số 3: metric circuit-breaker của Polly 8** — scope đòi alert "circuit breaker mở"; Polly 8 có telemetry riêng (`Polly.Extensions`?) nhưng tên meter/instrument phải probe thật từ chính pipeline payment đang chạy, không đoán docs — nếu Polly không phát metric tử tế, fallback: tự bắt event `OnOpened` của CircuitBreakerStrategyOptions publish counter riêng (vẫn thoả DoD).

### 4. Logs: Serilog → Elasticsearch (container sẵn có) → **Kibana** (thêm mới)
- **Tái dùng ES 8.15.3 của search** thay vì dựng ES thứ hai — 1 node ES gánh 2 vai (search index + log store) là trade-off có chủ đích ở local learning stack (AWS thật thì tách); rủi ro thật: heap đang để 512m — nâng lên 1g trong compose khi bắt đầu đổ log, và cấu hình retention/ILM tối thiểu để log không nuốt disk.
- **Rủi ro số 4: chọn sink đúng đời** — `Serilog.Sinks.Elasticsearch` cũ đã ngừng phát triển, bản kế nhiệm chính chủ là `Elastic.Serilog.Sinks` (Elastic maintain, ECS-compliant, ghi vào data stream thay vì index thường). Probe với ES 8.15.3 + xác nhận license (Apache-2.0? — kiểm tra thật, dự án có kỷ luật né trap license từ MassTransit/MediatR/Duende/ImageSharp) trước khi chốt.
- Log enrich `TraceId`/`SpanId` từ `Activity.Current` — đây là mắt xích nối 3 trụ: thấy lỗi trong Kibana → copy TraceId → mở đúng trace trên Jaeger. Console sink giữ nguyên song song (docker logs vẫn đọc được như 9 phase qua — không phá workflow debug hiện tại).
- Lambda: giữ `ILambdaContext.Logger` (CloudWatch Logs của LocalStack) — kéo Serilog+ES sink vào Lambda là thêm dependency nặng cho 7 handler chỉ để log ít dòng; ghi nhận là trade-off, backlog cùng mục "instrument trong Lambda".

### 5. Alert: **Grafana unified alerting** — không dựng Alertmanager riêng
- Cùng logic né collector ở mục 2: Grafana có alerting engine tích hợp đủ cho 2 rule DoD (circuit breaker mở; error rate payment cao) — thêm container Alertmanager + config routing chỉ để học YAML của nó là gold-plating (scope mục 12 cảnh báo đích danh). Alert "bắn" = trạng thái Firing thấy được trong Grafana UI (không SMTP thật — notification channel thật ghi backlog, nhất quán với "notification log-only" của cả dự án).
- Provisioning-as-code: datasource + dashboard + alert rule đều là file YAML/JSON trong `infrastructure/monitoring/grafana/` mount vào container — tái lập được từ scratch giống realm-export.json (Keycloak) và init script LocalStack; không click-ops trong UI rồi mất khi recreate.

### 6. Compose: file mới `docker-compose.monitoring.yml` (scope mục 8 định sẵn tên) — 4 container mới, tổng 21 → 25
- `jaeger` (all-in-one, in-memory store — trace mất khi restart, chấp nhận cho learning như LocalStack Community), `prometheus`, `grafana`, `kibana` (8.15.x khớp major ES). Include vào `docker-compose.yml` root như `docker-compose.infra.yml` hiện tại.
- Config nằm `infrastructure/monitoring/` (prometheus.yml, grafana provisioning, ILM policy nếu cần) — đúng cây thư mục scope mục 8 vẽ sẵn.

### 7. Gateway vào khuôn: gọi `AddTicketFlowWeb`/`UseTicketFlowWeb` (health + exception middleware) + observability như mọi service
- Gap tồn tại từ Phase 7 (gateway tự wire tay, không qua building block) — Phase 10 là lý do nghiệp vụ đầu tiên bắt buộc sửa: hop đầu của trace không thể là service duy nhất không instrument. Kiểm tra hồi quy: YARP + CORS + SignalR negotiate không vỡ khi thêm middleware.

---

## Nhóm A — Trace (DoD phần 1)

### Commit 1 — `chore(observability): TicketFlow.Observability + OTel trace → Jaeger (hop sync)` ✅ DONE
- **Probe rủi ro số 1 làm TRƯỚC building block, kết quả tốt hơn dự tính.** Standalone probe (MassTransit + RabbitMQ.Client thật, không mock) publish 1 message trong 1 `Activity` thủ công rồi bắt `ActivityListener` cả 2 phía: MassTransit 8.5.10 **tự propagate W3C trace context hoàn toàn**, không cần code thêm gì — cơ chế thật là header `MT-Activity-Id` (đúng format `traceparent`: `00-traceid-spanid-flags`) tự đính kèm lúc publish, tự đọc lại lúc consume, tạo `Activity` con đúng parent. Bonus phát hiện thêm: RabbitMQ.Client 7.x tự nó cũng phát `ActivitySource` riêng (`RabbitMQ.Client.Publisher`/`.Subscriber`) cho đúng lớp publish/deliver ở tầng broker, cùng 1 trace id. Kết luận: Commit 2 chỉ cần thêm đúng 1 dòng `.AddSource("MassTransit")`, không phải tự nhét header thủ công như phương án dự phòng đã ghi ở mục 2 — giảm hẳn rủi ro/độ khó Commit 2.
- Tạo `TicketFlow.Observability` (`AddTicketFlowObservability(configuration, serviceName)`: `AddAspNetCoreInstrumentation` + `AddHttpClientInstrumentation` + `AddGrpcClientInstrumentation` + OTLP exporter thẳng Jaeger). **`OpenTelemetry.Instrumentation.GrpcNetClient` không có bản stable nào** — `dotnet add package` từ chối resolve bản ổn định, chỉ có `1.17.0-beta.1`; xác nhận đây là tình trạng lâu dài của chính package (không phải gap tạm thời do pin lúc này), chấp nhận bản beta vì không có lựa chọn thật nào khác — ghi rõ lý do trong `Directory.Packages.props`.
- `docker-compose.monitoring.yml` với `jaeger` (all-in-one, OTLP gRPC 4317 + HTTP 4318, `COLLECTOR_OTLP_ENABLED=true`) — xác nhận thật qua log container: Jaeger v1.76.0 tự bật OTLP receiver mặc định, healthcheck pass ngay.
- Wire `AddTicketFlowObservability` vào cả 11 process (10 API + gateway). **Gateway vào khuôn `AddTicketFlowWeb`/`UseTicketFlowWeb` lần đầu** (gap có từ Phase 7) — giữ nguyên thứ tự `UseCors()` trước `UseTicketFlowWeb()` (CORS phải đứng trước Auth, lý do đã ghi từ Phase 8 Commit 6), đánh đổi duy nhất: CORS middleware không còn nằm trong `try/catch` của `ExceptionHandlingMiddleware` (chấp nhận được — CORS middleware thực tế không throw).
- `ExceptionHandlingMiddleware.traceId` đổi từ `context.TraceIdentifier` (Kestrel connection id, dạng `0HN...`) sang `Activity.Current.TraceId` — verify thật: response lỗi 400 trả `traceId` 32-hex thật, dán thẳng vào Jaeger tìm ra đúng trace.

**Verify thật (2 trace thật trên Jaeger, không đoán từ code):**
- **Hop HTTP xuyên YARP:** `GET /catalog/events` qua gateway → 1 trace id (`79572c83...`) chứa `gateway` (server span, root) → `gateway` (client span gọi `catalog:8002`) → `catalog` (server span `/events/`) — đúng 1 trace liền mạch qua reverse proxy, không đứt.
- **Hop gRPC ordering→reservation:** mua vé thật (hold → order → `Confirmed`, xác nhận saga RabbitMQ vẫn chạy đúng, Commit 1 không phá luồng cũ) + gọi trực tiếp `POST /ordering/check-seats` (điểm gọi gRPC thật) → 1 trace id (`e34097b3...`) xuyên `gateway` → `ordering` (server) → `ordering` (gRPC client span, tên thật `ticketflow.reservation.v1.ReservationService/CheckSeatAvailability`) → `reservation` (gRPC server span, `grpc.status=0`) — cùng trace id ở cả 2 đầu client/server, chứng minh context propagate đúng qua gRPC, không phải 2 span độc lập trùng ngẫu nhiên.
- **Ranh giới RabbitMQ (đúng dự tính, chưa phải lỗi):** trace mua vé thật dừng lại đúng tại span `ordering` xử lý `POST /orders/` — không có span nào tiếp nối vào saga dù đơn vẫn `Confirmed` thật ở cuối. Xác nhận rõ đây là khoảng trống Commit 2 sẽ lấp (`.AddSource("MassTransit")`), không phải bug của Commit 1.
- Không có exception/error nào liên quan OTel trong log 11 service (chỉ 2 noise nền đã biết từ trước, không liên quan: Npgsql GSSAPI probe `libgssapi_krb5.so.2`, và Kafka `connection refused` thoáng qua lúc `analytics` khởi động trước khi `kafka` sẵn sàng — cả hai tự hết, không phải do Commit 1).
- `docker compose ps` 22 container (21 cũ + `ticketflow-jaeger`). Regression: **81/81** (76 unit/domain + 5 Reservation integration). `dotnet build` cả solution sạch.

### Commit 2 — `feat(observability): trace xuyên hop async — saga RabbitMQ, Kafka Rider, Lambda Invoke (DoD trace khép)`
- `.AddSource("MassTransit")` + xử lý theo kết quả probe rủi ro số 1/số 2 (nếu Rider không propagate → tự nhét header qua producer/consumer filter, ghi rõ finding). Client span Lambda Invoke qua HttpClient instrumentation (mục 2). Custom `ActivitySource` cho các đoạn nghiệp vụ đáng nhìn (saga consumer, render PDF client).
- **Verify thật (DoD phần 1 khép ở đây):** 1 lần mua vé thật → **một trace duy nhất trên Jaeger** đi từ gateway qua ordering/reservation/payment/ticketing (xuyên các hop RabbitMQ của saga) tới span invoke RenderTicketPdf; kịch bản lỗi: dừng LocalStack → mua vé → trace hiện span lỗi + đường compensation (`RefundPayment`/`ReleaseSeats`) trong cùng trace; SaleRecorded → analytics qua Kafka có trace (hoặc finding ghi rõ nếu Rider không propagate); regression 81/81.

## Nhóm B — Metrics + dashboard

### Commit 3 — `feat(observability): Prometheus + Grafana — RED + "vé bán/phút"`
- `/metrics` (Prometheus exporter) mọi service; `prometheus` + `grafana` container; provisioning-as-code (mục 5); dashboard: RED per-service + business panel "vé bán/phút" từ counter `ticketflow.tickets.sold` (Meter mới trong Ordering, mục 3).
- **Verify thật:** Prometheus targets 11/11 UP; mua N vé thật → counter tăng đúng N ghế, panel Grafana nhảy số thật; RED phản ánh request thật (bắn 1 loạt request lỗi 4xx/5xx chủ đích → thấy trên dashboard); `docker compose ps` 24 container.

## Nhóm C — Logs tập trung

### Commit 4 — `feat(observability): Serilog → Elasticsearch → Kibana, log nối trace`
- Probe rủi ro số 4 (sink đời mới + license) trước. Serilog vào `TicketFlow.Observability` (console giữ nguyên + ES sink, enrich TraceId/SpanId); `kibana` container + data view; nâng heap ES 512m→1g + retention tối thiểu (mục 4).
- **Verify thật:** mua vé thật → log của ≥4 service hiện trong Kibana, structured, cùng `TraceId`; lấy TraceId từ 1 dòng log lỗi thật (ép lỗi payment) mở đúng trace trên Jaeger — mắt xích log↔trace chạy thật; search của search-service không vỡ (ES gánh 2 vai vẫn khoẻ); `docker compose ps` 25 container; regression 81/81.

## Nhóm D — Alert + finalize

### Commit 5 — `chore: finalize Phase 10 — alert rules, scope reconcile, DoD`
- Probe rủi ro số 3 (metric Polly) → 2 alert rule provisioned: **circuit breaker payment mở** + **error rate payment vượt ngưỡng**; ép lỗi thật (payment fail liên tục cho breaker mở) → alert chuyển Firing thật trong Grafana.
- Scope reconcile: sửa mục 12 ("Observability sớm từ Phase 3" — không xảy ra, ghi thật); mục 8 tick `TicketFlow.Observability` đã tồn tại; cập nhật bảng hạ tầng nếu lệch. Tick DoD, cập nhật doc này với finding thật từng commit.

---

## Sơ đồ Phase 10

```mermaid
flowchart LR
    subgraph Services[".NET x12 — AddTicketFlowObservability"]
        GW[gateway] --- SVC[10 API services]
    end
    subgraph Mon["docker-compose.monitoring.yml (+4 container)"]
        JG[Jaeger all-in-one]
        PR[Prometheus]
        GF[Grafana<br/>dashboard + alerting]
        KB[Kibana]
    end
    Services -->|"OTLP (trace)"| JG
    PR -->|"scrape /metrics"| Services
    Services -->|"Serilog ES sink<br/>(enrich TraceId)"| ES[(Elasticsearch 8.15.3<br/>sẵn có — search + logs)]
    KB --> ES
    GF --> PR
    RB[[RabbitMQ saga hops]] -. "traceparent header<br/>(probe #1)" .-> JG
    LS[Lambda Invoke] -. "client span<br/>(trong Lambda = backlog)" .-> JG
```

---

## Kiểm chứng DoD cuối Phase 10

| DoD | Kiểm chứng |
|---|---|
| 1 lần mua vé → trace xuyên suốt mọi service trên Jaeger | Commit 2: 1 trace id duy nhất từ gateway xuyên saga RabbitMQ tới span Lambda invoke, cả happy path lẫn compensation |
| Dashboard hoạt động | Commit 3: RED + "vé bán/phút" nhảy số từ purchase thật; Commit 4: Kibana log nối Jaeger qua TraceId |
| Alert hoạt động | Commit 5: ép breaker payment mở thật → rule Firing trong Grafana |

## Hướng mở rộng (backlog — không thuộc Phase 10)
- **OTel bên trong Lambda** (traceparent qua payload + export từ executor container) + Serilog cho Functions — mục 2/4.
- **OpenTelemetry Collector** khi cần multi-backend/tail-sampling thật — mục 2.
- **Alert notification channel thật** (SMTP/Slack) — Grafana Firing là đủ cho DoD; nhất quán notification log-only.
- **Browser-side tracing** (frontend Next.js) + trace SignalR tới client — mục 2.
- **Tách ES search khỏi ES logs** khi lên hạ tầng thật — mục 4.
- **Persistent trace store** (Jaeger + backend thật thay in-memory) — cùng tiền lệ LocalStack/Keycloak dev không persist.

## Thứ tự đọc trước khi code (scope mục 13)
Đọc **Ch16 (Observability)** — 3 trụ trace/metric/log + RED. Bổ sung ngoài sách: **OpenTelemetry .NET** (SDK vs instrumentation packages — nhịp version khác nhau, pin exact), **W3C Trace Context** (`traceparent` — nền của mọi câu hỏi propagation trong plan này), **MassTransit DiagnosticSource** (`ActivitySource "MassTransit"` — rủi ro #1), **Jaeger OTLP native**, **Prometheus pull model + naming convention**, **Grafana provisioning + unified alerting**, **Elastic.Serilog.Sinks vs Serilog.Sinks.Elasticsearch** (đời sink + license — rủi ro #4), **Polly 8 telemetry** (rủi ro #3).
