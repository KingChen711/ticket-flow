# Rà soát bảo mật cơ bản

Ngày rà: 2026-10-24. Repo đang **public** trên GitHub, nên mọi giá trị dưới đây coi như đã lộ.
File này **không chứa giá trị secret nào**, chỉ có vị trí và loại.

## 1. Secret trong file hiện tại và lịch sử git

### Cách quét

- `gitleaks git .` (v8.21.2) trên toàn bộ 50 commit: không phát hiện gì.
- `gitleaks dir .`: chỉ báo trong thư mục build `.next/` trên máy (đã nằm trong `.gitignore`, không có trong repo).
- Vì gitleaks không nhận ra mật khẩu dạng chữ thường và client secret Keycloak, quét bổ sung bằng script regex trên mọi file được git theo dõi (khóa JSON `secret`/`password`/`ClientSecret`/`AccessKey`/`SecretKey`, biến `*_PASSWORD`, `Password=` trong chuỗi kết nối, `authSecret`, `BasicAWSCredentials`...).
- So sánh mọi giá trị giống secret từng được thêm vào trong `git log --all -p` với HEAD (so bằng hash): không có giá trị nào chỉ còn trong lịch sử. Nghĩa là lịch sử không giấu thêm secret nào ngoài những gì đang thấy ở HEAD, và xóa khỏi HEAD là chưa đủ, vẫn phải xoay khóa nếu đã dùng ở đâu khác.
- Không có file `.env` nào từng được commit.

### Kết quả

Tất cả đều là giá trị dev cho máy local (Docker Compose, kind), không thấy khóa cloud thật hay token dịch vụ ngoài.
Rủi ro thật nằm ở chỗ: ai đó dùng lại các giá trị này cho môi trường có thật, hoặc dựng stack ở nơi truy cập được từ Internet.

| File | Dòng | Loại |
|---|---|---|
| `infrastructure/keycloak/realm-export.json` | 51, 63, 78, 93 | Client secret Keycloak (identity-admin, customer-web, organizer-dashboard, gate-scanner) |
| `infrastructure/keycloak/realm-export.json` | 230, 240, 250 | Mật khẩu user mẫu (customer1, organizer1, admin1) |
| `infrastructure/k8s/ticketflow/files/realm-export.json` | 51, 63, 78, 93 | Bản sao client secret Keycloak |
| `infrastructure/k8s/ticketflow/files/realm-export.json` | 230, 240, 250 | Bản sao mật khẩu user mẫu |
| `infrastructure/k8s/ticketflow/values.yaml` | 151, 160, 169 | `AUTH_SECRET` của NextAuth (khóa ký/mã hóa session của 3 app) |
| `infrastructure/k8s/ticketflow/values.yaml` | 153, 162, 171 | Client secret Keycloak (trùng với realm-export) |
| `infrastructure/k8s/ticketflow/values.yaml` | 9, 19, 27, 40, 46 | Mật khẩu Postgres, RabbitMQ, MinIO, MongoDB, ClickHouse |
| `infrastructure/k8s/ticketflow/values.yaml` | 53 | Mật khẩu admin Keycloak |
| `src/Services/Identity/TicketFlow.Identity.Api/appsettings.json` | 22 | Client secret Keycloak của service account identity-admin (quyền Admin API) |
| `infrastructure/docker/docker-compose.infra.yml` | 7, 35, 71, 121, 137 | Mật khẩu Postgres, RabbitMQ, MinIO, MongoDB, ClickHouse |
| `infrastructure/docker/docker-compose.infra.yml` | 157 | Mật khẩu admin Keycloak |
| `infrastructure/docker/docker-compose.monitoring.yml` | 60 | Mật khẩu admin Grafana |
| `src/Services/*/TicketFlow.*.Api/appsettings.json` và `appsettings.Development.json` (Analytics, Catalog, Identity, Notification, Ordering, Payment, Recommendation, Reservation, Search, Ticketing) | xem lệnh dưới | Chuỗi kết nối Postgres/MongoDB/ClickHouse có mật khẩu, mật khẩu RabbitMQ |
| `src/Services/Ticketing/TicketFlow.Ticketing.Api/appsettings.json` | 19, 20 | Access key / secret key MinIO |
| `src/Services/Ticketing/TicketFlow.Ticketing.Api/appsettings.Development.json` | 18, 19 | Access key / secret key MinIO |
| `infrastructure/localstack/init/04-deploy-notification.sh` | 53 | Chuỗi kết nối RabbitMQ có mật khẩu (truyền vào Lambda) |
| `src/Gateway/TicketFlow.Gateway/Program.cs` | 29 | Khóa AWS giả cho LocalStack (giá trị mặc định công khai của LocalStack) |
| `src/Services/Ticketing/TicketFlow.Ticketing.Infrastructure/ServiceCollectionExtensions.cs` | 31 | Khóa AWS giả cho LocalStack |

Liệt kê lại đúng các dòng trong appsettings (chỉ in tên file và số dòng):

```bash
git grep -n -I -E 'Password=|"Password"\s*:|mongodb://[^"]*:[^"]*@' -- 'src/**/appsettings*.json' | cut -d: -f1,2
```

### Việc chủ repo nên làm

1. Nếu bất kỳ giá trị nào ở trên từng được dùng cho môi trường khác ngoài máy local (server thật, Keycloak thật, tài khoản cloud), **xoay khóa ngay**: tạo client secret mới trong Keycloak, đổi mật khẩu DB/broker, tạo `AUTH_SECRET` mới (`openssl rand -base64 32`).
2. Chỉ giữ giá trị dev trong repo khi đã ghi rõ là dev (realm-export và compose hiện đã thế). Với `values.yaml` và `appsettings.json` (bản Docker), cân nhắc chuyển sang biến môi trường hoặc `--set`/Secret ngoài chart để không ai triển khai nhầm với giá trị mặc định.
3. Bật GitHub secret scanning và push protection cho repo (Settings → Code security).

Không xóa gì khỏi lịch sử git (đúng yêu cầu: không viết lại lịch sử, không force push).

## 2. Lỗ hổng dependency

### npm (`pnpm audit`, pnpm 11)

Tổng: 8 critical, 28 high, 15 moderate, 1 low. Các package có critical/high:

| Package | Bản đang dùng | Critical / High | Bản vá | Kéo vào bởi |
|---|---|---|---|---|
| `next` | 16.2.10 | 5 / 6 (RCE trong Image Optimization, `use cache`, `next/og`; SSRF; DoS; bypass middleware) | >= 16.4.1 | trực tiếp, cả 3 app |
| `next-auth` | 5.0.0-beta.31 | 2 / 1 (lỗi cấu hình làm kiểm tra auth sai, chuẩn hóa email, `getToken()` crash) | >= 5.0.0-beta.32 | trực tiếp, cả 3 app |
| `@auth/core` | 0.41.2 | 1 / 1 | >= 0.41.3 | qua `next-auth` |
| `sharp` | 0.34.5 | 0 / 3 (libvips, libheif, librsvg) | >= 0.35.5 | qua `next` |
| `postcss` | 8.4.31 | 0 / 2 | >= 8.5.23 | qua `next`, `@tailwindcss/postcss` |
| `brace-expansion` | 1.x, 5.0.7 | 0 / 8 (DoS) | >= 1.1.21 / >= 5.0.12 | qua `eslint` (chỉ lúc dev) |
| `js-yaml` | 4.3.0 | 0 / 2 (DoS) | >= 4.3.2 | qua `eslint` (chỉ lúc dev) |
| `browserslist` | 4.28.6 | 0 / 2 | >= 4.28.7 | qua `next`, `eslint-config-next` |
| `nanoid` | 3.3.16 | 0 / 1 | >= 3.3.18 | qua `postcss` |
| `source-map-js` | 1.2.1 | 0 / 1 | >= 1.2.2 | qua `postcss` |
| `zod` | 4.4.3 | 0 / 1 | >= 4.6.6 | qua `eslint-config-next` (chỉ lúc dev) |

Ưu tiên: `next` và `next-auth` (chạy ở runtime, có lỗi RCE và lỗi xác thực). Cả hai đều có bản vá cùng major
(16.x và 5.0.0-beta.x). Theo yêu cầu, **chưa nâng version**; gợi ý cho chủ repo:

```bash
pnpm -r up next@^16.4.1 eslint-config-next@^16.4.1 next-auth@5.0.0-beta.32
pnpm audit
pnpm lint && pnpm build   # rồi thử lại đăng nhập Keycloak trên cả 3 app
```

### Cập nhật sau khi nâng cấp (PR 8)

`next` 16.2.10 → 16.4.2, `eslint-config-next` → 16.4.2, `next-auth` 5.0.0-beta.31 → 5.0.0-beta.32, cùng các package
bắc cầu được nâng trong range semver sẵn có. `pnpm audit` sau nâng cấp: **0 critical, 6 high, 2 moderate**. Phần còn lại:

| Package | Bản | Vì sao chưa vá | Dùng lúc |
|---|---|---|---|
| `brace-expansion` | 5.0.7 | `minimatch` (qua `@typescript-eslint`) khóa range | lint |
| `browserslist` | 4.28.6 | `@babel/helper-compilation-targets` (qua `styled-jsx`) khóa range | build |
| `baseline-browser-mapping` (moderate) | 2.10.43 | `next` khóa range | build |

Vá nốt cần `pnpm.overrides`, không làm để tránh ép version mà package cha chưa kiểm.

### NuGet (`dotnet list package --vulnerable --include-transitive`)

| Package | Bản | Mức | Advisory | Project | Ghi chú |
|---|---|---|---|---|---|
| `SSH.NET` | 2025.1.0 | High | GHSA-mggc-4xg6-vcxf, GHSA-q939-rpr3-3284 | `TicketFlow.Reservation.IntegrationTests` | phụ thuộc bắc cầu của Testcontainers, chỉ dùng khi test |
| `MessagePack` | 2.5.302 | Moderate | GHSA-qhrr-8q5h-9q3h | `Reservation.LoadTests`, `FlashSale.LoadTests` | bắc cầu từ NBomber, chỉ dùng khi load test |

Không có lỗ hổng NuGet nào trong project chạy thật (service, gateway, Lambda).

## 3. Lỗi bảo mật logic phát hiện khi đọc code

Không phải phạm vi quét tự động, ghi lại để không bị bỏ sót:

- Danh sách ghế trong đơn hàng không được đối chiếu với hold: đã chứng minh bằng test và sửa ở PR 3.
- `TotalAmount` của đơn hàng do client gửi lên, không đối chiếu giá trong catalog: đã sửa ở PR 9, ordering tự tính giá từ hạng giá của catalog.
