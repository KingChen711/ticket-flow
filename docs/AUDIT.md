# Khảo sát repo TicketFlow

Ngày khảo sát: 2026-10-24, trên commit `73454c7` (nhánh `main`).

## App làm gì

Nền tảng bán vé sự kiện (concert, thể thao) theo kiến trúc microservices, làm để học các pattern
trong sách *Microservices Design Patterns in .NET*. Luồng chính có trong code:

- Organizer tạo venue, sự kiện, sơ đồ ghế và publish sự kiện (catalog).
- Khách xem/tìm sự kiện, chọn ghế, giữ chỗ có TTL 10 phút bằng Lua script nguyên tử trên Redis (reservation).
- Khách đặt đơn, `PurchaseSaga` (MassTransit, RabbitMQ) điều phối: chốt ghế, thanh toán (cổng giả lập, bọc Polly), phát vé PDF có QR (Lambda trên LocalStack, lưu MinIO), gửi thông báo (chỉ ghi log). Có nhánh bù trừ: hoàn tiền, nhả ghế, hủy đơn.
- Đơn hàng lưu theo event sourcing (Marten trên Postgres), read model "đơn của tôi" tách riêng (CQRS).
- Nhân viên cổng quét QR để check-in (gate-scanner).
- Phụ trợ: search (Elasticsearch), gợi ý đơn giản (MongoDB), doanh số (Kafka sang ClickHouse), nhắc lịch 24h (Lambda hẹn giờ), upload poster và thumbnail (Lambda).
- Xác thực bằng Keycloak (OIDC), gateway YARP kiểm JWT theo route.

## Cấu trúc thư mục

```
src/BuildingBlocks/   thư viện dùng chung: Domain, Messaging, Web (auth, middleware), Observability, Contracts.Messaging
src/Services/<Tên>/   10 service, mỗi service chia Api / Application / Domain / Infrastructure (+ *.Tests)
src/Gateway/          YARP gateway, gọi thẳng Lambda media
src/Functions/        AWS Lambda .NET (PDF vé, poster, nhắc lịch, 2 hàm probe Hello*)
apps/                 3 app Next.js 16: customer-web (shell Multi-Zones), organizer-dashboard, gate-scanner
packages/             types (dùng thật), ui (rỗng)
contracts/protos/     reservation.proto (gRPC ordering sang reservation)
infrastructure/       docker compose hạ tầng + monitoring, Keycloak realm, LocalStack init, Helm chart cho kind
tests/                load test NBomber flash sale (chạy tay, cần cả stack)
docs/                 kế hoạch và nhật ký từng phase (phase-1..11)
scope.md              tài liệu phạm vi và kế hoạch tổng
```

## Cách chạy hiện tại

- Repo không có README. Cách chạy chỉ suy ra được từ `scope.md`, `docs/phase-*.md`, `docker-compose.yml`.
- Yêu cầu thực tế: .NET SDK 10, Node 22 + pnpm, Docker. Full stack là khoảng 21 container (Postgres, Redis, RabbitMQ, Kafka, MinIO, Elasticsearch, MongoDB, ClickHouse, Keycloak, LocalStack, Jaeger, Prometheus, Grafana, Kibana và 11 service .NET).
- `docker compose up` cần biến `LOCALSTACK_AUTH_TOKEN` (tài khoản LocalStack) và phải build `function.zip` trước bằng `dotnet lambda package` (cần `Amazon.Lambda.Tools`), nếu không các tính năng PDF vé/poster/nhắc lịch không có.
- 3 app frontend không nằm trong `docker-compose.yml`, phải chạy `pnpm dev` riêng với `.env.local` tự tạo. Không có file `.env.example`, tên biến chỉ tìm thấy khi đọc `auth.ts`, `next.config.ts`, `values.yaml`.
- Đã kiểm trong môi trường sạch (Ubuntu 24.04, .NET SDK 10.0.113, Node 22.22, pnpm 10.28):
  - `dotnet restore` + `dotnet build TicketFlow.slnx -c Release`: thành công, 0 lỗi, 6 cảnh báo NuGet (2 package thừa, 3 cảnh báo lỗ hổng).
  - `pnpm install --frozen-lockfile`, `pnpm lint`, `pnpm build`: thành công cả 3 app.
  - Chưa kiểm được `docker compose up` toàn bộ: môi trường khảo sát chặn `quay.io` (image Keycloak) và không có token LocalStack. Kết luận: repo build được, chạy full stack phụ thuộc dịch vụ ngoài.

## Mức độ test

| Project | Loại | Số test | Kết quả |
|---|---|---|---|
| Catalog.Domain.Tests | unit domain | 35 | xanh |
| Ordering.Domain.Tests | unit domain (fold event) | 9 | xanh |
| Ordering.Application.Tests | handler + `PurchaseSaga` (MassTransit test harness, in-memory) | 14 | xanh, mất khoảng 2 phút |
| Payment.Domain.Tests | unit domain | 9 | xanh |
| Payment.Infrastructure.Tests | Polly pipeline | 3 | xanh |
| Ticketing.Domain.Tests | unit domain | 6 | xanh |
| Reservation.IntegrationTests | Redis thật qua Testcontainers (chống oversell) | 5 | xanh khi có Docker |
| Reservation.LoadTests, FlashSale.LoadTests | NBomber, chạy tay | - | không chạy trong `dotnet test` |

- Tổng 81 test tự động, đều xanh.
- Không có test cho: `ReservationService` (hold/confirm/unsell, phần lõi chống bán trùng), domain `Seat`, Identity, Search, Notification, Recommendation, Analytics, Gateway (phân quyền theo route), Lambda functions.
- 3 app frontend: 0 test, chỉ có ESLint và type check khi build.

## Nợ kỹ thuật lớn nhất (xếp theo mức ảnh hưởng)

1. **Saga phát vé và hoàn ghế theo danh sách ghế client gửi, không theo ghế thực sự được chốt từ hold.** `PlaceOrderRequest.SeatIds` đi thẳng vào `OrderSubmitted`; `ConfirmHoldScript` chỉ chốt ghế thuộc hold, nhưng `PurchaseSaga` bỏ qua `SeatsConfirmed.SeatIds` và dùng lại danh sách cũ cho `IssueTickets` và `ReleaseSeats`. Hệ quả: gửi thêm id ghế người khác đã mua có thể nhận vé cho ghế đó, hoặc khi thanh toán lỗi thì nhả luôn ghế của người khác (bán trùng). (`src/Services/Ordering/TicketFlow.Ordering.Application/Sagas/PurchaseSaga.cs`)
2. **Giá đơn hàng do client quyết định.** `TotalAmount` lấy từ body request, không đối chiếu với `PriceTier` của catalog. Cổng thanh toán là giả lập nên chưa mất tiền thật, nhưng là lỗ hổng nghiệp vụ. (`src/Services/Ordering/TicketFlow.Ordering.Api/Endpoints/OrderEndpoints.cs`)
3. **CI chưa từng chạy.** `.github/workflows/ci.yml` chỉ kích hoạt trên nhánh `master`, trong khi nhánh mặc định là `main`; GitHub Actions có 0 lần chạy. Workflow cũng không lint/build phần frontend.
4. **Dependency frontend có lỗ hổng nghiêm trọng.** `pnpm audit`: 8 critical, 28 high, chủ yếu từ `next@16.2.10` (nhiều lỗi RCE/SSRF, bản vá từ 16.4.1) và `next-auth@5.0.0-beta.31` (bản vá beta.32).
5. **Secret dev nằm trong repo public.** Client secret Keycloak, mật khẩu user mẫu, `AUTH_SECRET` của NextAuth, mật khẩu hạ tầng nằm trong `realm-export.json`, `values.yaml`, `appsettings*.json`. Đều là giá trị dev, nhưng ai dùng lại cho môi trường thật sẽ bị lộ. Chi tiết ở PR bảo mật.
6. **Ghi chéo nhiều store không nguyên tử, chưa có outbox ở ordering.** Đặt đơn: lưu Marten rồi mới publish `OrderSubmitted` (crash ở giữa thì đơn treo Pending mãi). Reservation: chốt Redis rồi mới lưu Postgres; provision inventory: lưu Postgres rồi mới nạp Redis (crash ở giữa thì sự kiện không giữ ghế được và cũng không provision lại được). Các chỗ này đã được tác giả ghi chú trong code.
7. **Không có hướng dẫn chạy cho người lạ.** Không README, không `.env.example`; full stack cần token LocalStack và bước build Lambda thủ công; frontend chạy riêng ngoài compose.
8. **Thiếu test cho phần lõi reservation và phân quyền.** Logic `ReservationService` và phân quyền route ở gateway chưa có test tự động; frontend không có test nào.
9. **Tài liệu nặng nhật ký, khó đọc với người ngoài.** `scope.md` và `docs/phase-*.md` dài và chi tiết, comment trong code thường kể lịch sử commit ("Commit 9", "Phase 5") thay vì giải thích code hiện tại.
10. **Rác nhỏ:** `TicketFlow.Catalog.Api.http` còn từ template (gọi `/weatherforecast` không tồn tại), 2 `PackageReference` thừa ở `TicketFlow.Observability` (cảnh báo NU1510), `dotnet format --verify-no-changes` báo lệch thứ tự `using` ở nhiều file (không sửa hàng loạt).
