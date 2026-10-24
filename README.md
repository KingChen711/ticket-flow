# TicketFlow

Nền tảng bán vé sự kiện viết bằng .NET 10 microservices và Next.js 16. Mục đích chính là học và thực hành
các pattern microservices (saga, event sourcing, CQRS, outbox, chống bán trùng ghế).

## Yêu cầu

| Công cụ | Phiên bản đã kiểm | Dùng cho |
|---|---|---|
| .NET SDK | 10.0.x | build, test, chạy service |
| Node.js | 22.x | 3 app frontend |
| pnpm | 10.x | monorepo frontend |
| Docker + Docker Compose v2 | Docker 29 | hạ tầng và service; test Redis dùng Testcontainers |
| RAM trống cho Docker | khoảng 8 GB | full stack khoảng 21 container (Elasticsearch riêng đã 1 GB) |

Tùy chọn (chỉ khi chạy full stack có Lambda): `Amazon.Lambda.Tools` và một token LocalStack miễn phí.

## 1. Build và test (không cần Docker cho phần lớn test)

```bash
git clone https://github.com/KingChen711/ticket-flow.git
cd ticket-flow

# Backend .NET
dotnet restore TicketFlow.slnx
dotnet build TicketFlow.slnx -c Release --no-restore
dotnet test TicketFlow.slnx -c Release --no-build

# Frontend
pnpm install --frozen-lockfile
pnpm lint
pnpm build
```

`dotnet test` chạy cả `TicketFlow.Reservation.IntegrationTests`, project này cần Docker đang chạy (Testcontainers
tự kéo image `redis:7`). Không có Docker thì riêng project đó báo lỗi, các project còn lại vẫn chạy.
Chỉ chạy test không cần Docker:

```bash
dotnet test TicketFlow.slnx -c Release --filter "FullyQualifiedName!~IntegrationTests"
```

Chạy riêng test của luồng mua vé:

```bash
dotnet test src/Services/Reservation/TicketFlow.Reservation.Application.Tests   # giữ / chốt / nhả ghế
dotnet test src/Services/Reservation/TicketFlow.Reservation.IntegrationTests    # Redis thật, chống bán trùng (cần Docker)
dotnet test src/Services/Ordering/TicketFlow.Ordering.Application.Tests         # đặt đơn + PurchaseSaga
```

## 2. Chạy backend bằng Docker Compose

```bash
cp .env.example .env          # điền LOCALSTACK_AUTH_TOKEN nếu có (xem bên dưới)

# Chỉ cần khi muốn có PDF vé / poster / nhắc lịch (Lambda trên LocalStack):
dotnet tool install -g Amazon.Lambda.Tools
(cd src/Functions/TicketFlow.Functions && dotnet lambda package -c Release -o function.zip)

docker compose up -d --build
```

Lần đầu mất vài phút để kéo image và build 11 image .NET. Kiểm nhanh:

```bash
curl http://localhost:8080/health          # gateway
curl http://localhost:8080/catalog/events  # danh sách sự kiện (route ẩn danh)
```

Các service tự chạy migration và catalog tự seed dữ liệu mẫu khi khởi động.

Không có `LOCALSTACK_AUTH_TOKEN` thì container LocalStack không lên; mọi thứ khác vẫn chạy, nhưng bước phát vé của
saga gọi Lambda sẽ lỗi nên đơn hàng đi vào nhánh bù trừ (hoàn tiền, nhả ghế, hủy đơn) thay vì `Confirmed`.

### Địa chỉ hữu ích

| Thành phần | URL |
|---|---|
| API gateway | http://localhost:8080 |
| Keycloak (admin console) | http://localhost:8180 |
| RabbitMQ management | http://localhost:15672 |
| Jaeger (trace) | http://localhost:16686 |
| Grafana | http://localhost:3300 |
| Kibana (log) | http://localhost:5601 |
| Prometheus | http://localhost:19090 |
| MinIO console | http://localhost:19001 |

Mật khẩu dev của hạ tầng nằm trong `infrastructure/docker/*.yml`. Đây là giá trị chỉ dành cho máy local.

### Tài khoản mẫu (Keycloak realm `ticketflow`)

`customer1` (role customer), `organizer1` (role organizer), `admin1` (role admin). Mật khẩu dev nằm trong
`infrastructure/keycloak/realm-export.json`, xem bằng:

```bash
jq -r '.users[] | "\(.username): \(.credentials[0].value)"' infrastructure/keycloak/realm-export.json
```

## 3. Chạy frontend

Frontend không nằm trong Compose, chạy trên máy host và gọi gateway ở `localhost:8080`.

```bash
for app in customer-web organizer-dashboard gate-scanner; do
  cp apps/$app/.env.example apps/$app/.env.local
done
# Điền AUTH_SECRET (openssl rand -base64 32) và AUTH_KEYCLOAK_SECRET (lệnh jq có trong từng file) cho mỗi app.

pnpm dev
```

| App | URL | Đăng nhập bằng |
|---|---|---|
| customer-web | http://localhost:3000 | customer1 |
| organizer-dashboard | http://localhost:3001/organizer (hoặc qua shell http://localhost:3000/organizer) | organizer1 |
| gate-scanner | http://localhost:3002/scanner (hoặc qua shell http://localhost:3000/scanner) | organizer1 |

## Biến môi trường

- `.env.example` (gốc repo): biến cho Docker Compose.
- `apps/<app>/.env.example`: biến cho từng app Next.js.
- Cấu hình .NET nằm trong `appsettings.json` (chạy trong Docker) và `appsettings.Development.json` (chạy `dotnet run` trên host).

## Dừng và dọn

```bash
docker compose down        # giữ dữ liệu
docker compose down -v     # xóa luôn volume (Postgres, Mongo, ES, MinIO...)
```
