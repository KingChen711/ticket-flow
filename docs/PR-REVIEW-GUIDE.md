# Hướng dẫn review chuỗi PR "polish"

11 PR được xếp chồng (stacked): mỗi PR lấy nhánh của PR trước làm base, PR đầu tiên lấy `chore/polish`
(tạo từ `main` tại commit `73454c7`). Diff của mỗi PR chỉ chứa thay đổi của chính nó.

## Thứ tự nên đọc và merge

| # | PR | Nhánh | Loại thay đổi | Cần đọc kỹ |
|---|---|---|---|---|
| 1 | [#1 Khảo sát](https://github.com/KingChen711/ticket-flow/pull/1) | `chore/polish-1-audit` | chỉ tài liệu | mục "Nợ kỹ thuật" |
| 2 | [#2 README chạy được + .env.example](https://github.com/KingChen711/ticket-flow/pull/2) | `chore/polish-2-run` | tài liệu, `.gitignore` của 3 app | các bước chưa kiểm được (compose up, đăng nhập) |
| 3 | [#3 Test + sửa bug saga](https://github.com/KingChen711/ticket-flow/pull/3) | `chore/polish-3-tests` | test mới, **1 thay đổi hành vi** trong `PurchaseSaga.cs` | **đọc kỹ nhất**: commit `fix(ordering)` |
| 4 | [#4 Dọn dẹp](https://github.com/KingChen711/ticket-flow/pull/4) | `chore/polish-4-cleanup` | xóa file/package, sửa Dockerfile, lockfile | Dockerfile frontend |
| 5 | [#5 CI](https://github.com/KingChen711/ticket-flow/pull/5) | `chore/polish-5-ci` | workflow | trigger và chi phí runner |
| 6 | [#6 Bảo mật](https://github.com/KingChen711/ticket-flow/pull/6) | `chore/polish-6-security` | chỉ tài liệu | danh sách secret cần xoay |
| 7 | [#7 README cuối + hướng dẫn này](https://github.com/KingChen711/ticket-flow/pull/7) | `chore/polish-7-final` | chỉ tài liệu | danh sách tính năng, hạn chế |
| 8 | [#8 Nâng next / next-auth](https://github.com/KingChen711/ticket-flow/pull/8) | `chore/polish-8-deps` | dependency frontend, lockfile | đăng nhập 3 app |
| 9 | [#9 Tính giá ở server](https://github.com/KingChen711/ticket-flow/pull/9) | `chore/polish-9-pricing` | **đổi hành vi** đặt đơn, cấu hình mới | ordering giờ gọi catalog + reservation khi đặt đơn |
| 10 | [#10 Database ordering cho Compose](https://github.com/KingChen711/ticket-flow/pull/10) | `chore/polish-10-ordering-db` | script init Postgres | volume cũ cần `down -v` |
| 11 | [#11 Đơn chốt thiếu ghế](https://github.com/KingChen711/ticket-flow/pull/11) | `chore/polish-11-order-seats` | **đổi hành vi** saga, event | nhánh "thiếu ghế" của `PurchaseSaga` |

Cách merge:

1. Merge lần lượt #1 → #11 vào base của từng PR (dùng "Create a merge commit" hoặc "Squash", đừng rebase nhánh của PR sau).
   Khi PR trước được merge và nhánh của nó bị xóa, GitHub tự đổi base của PR kế tiếp.
2. Mở một PR cuối `chore/polish` → `main` và merge.
3. Nếu chỉ muốn lấy một phần, có thể dừng ở bất kỳ PR nào; các PR sau phụ thuộc PR trước.

Nếu muốn đọc nhanh: #3, #9, #11 đổi hành vi app (đều có test chứng minh); #4, #5, #8, #10 đổi build/CI/dependency/hạ tầng; còn lại là tài liệu.

## 10 điểm chủ repo phải tự kiểm bằng tay trước khi merge

1. **Chạy full stack theo README trên máy của bạn.** `cp .env.example .env`, điền `LOCALSTACK_AUTH_TOKEN`, build `function.zip`, `docker compose up -d --build`, rồi `curl localhost:8080/health` và `/catalog/events`. Môi trường soạn PR không dựng được stack (bị chặn `quay.io`, `docker.elastic.co`, không có token LocalStack), nên đây là phần README chưa được kiểm thật.
2. **Đi hết hành trình mua vé trên UI với bản sửa saga (#3).** Đăng nhập `customer1`, giữ 1-2 ghế, đặt đơn, chờ `Confirmed`, tải PDF vé. Kiểm số vé bằng đúng số ghế đã giữ.
3. **Thử nhánh bù trừ sau bản sửa.** Ép thanh toán lỗi hoặc tắt LocalStack để phát vé lỗi; kiểm đơn chuyển `Cancelled`, ghế quay lại `Available` trên sơ đồ ghế, và **ghế của đơn khác không bị nhả**.
4. **Quyết định về `push` trong CI (#5).** Workflow vẫn nghe `push: [master]`. Nếu đổi sang `main`, mỗi lần merge sẽ đẩy 14 image lên GHCR và deploy thử lên kind. Kiểm tab Actions của PR #5 để thấy cả job build image đều xanh trước khi quyết.
5. **Xoay secret nếu từng dùng lại (#6).** Xem bảng trong `docs/SECURITY-AUDIT.md`. Nếu client secret Keycloak, `AUTH_SECRET`, hay mật khẩu nào trong đó từng được dùng ở nơi khác máy local, đổi ngay. Bật GitHub secret scanning và push protection.
6. **Thử đăng nhập sau khi nâng `next`/`next-auth` (#8)** trên cả 3 app: đăng nhập, để access token hết hạn (1 giờ) rồi thao tác lại để kiểm refresh token, đăng xuất, và luồng Multi-Zones (`localhost:3000/organizer`, `/scanner`). Đã smoke test redirect OIDC với IdP giả, chưa thử Keycloak thật.
7. **Chạy thử image frontend (#4).** CI của PR #5 đã build được cả 3 image sau khi Dockerfile bỏ dòng `COPY packages/ui`, nhưng chưa ai chạy chúng. Nếu dùng Helm chart, `helm install` trên kind và mở `localhost:3000` để chắc image vẫn khởi động và đăng nhập được.
8. **Chạy frontend dev bằng các file `.env.example` (#2).** Copy sang `.env.local`, điền secret bằng lệnh `jq` có trong file, `pnpm dev`, đăng nhập được cả 3 app. Kiểm callback OAuth của organizer/scanner quay về đúng `localhost:3001` / `3002`.
9. **Đọc lại README cuối (#7) với con mắt nhà tuyển dụng.** Mọi tính năng trong danh sách đều có trong code, nhưng bạn biết rõ nhất cái nào đã chạy ổn định; xóa dòng nào bạn không muốn bị hỏi khi phỏng vấn. Mục "Hạn chế đã biết" nên giữ.
10. **Quyết định các việc còn treo:** 2 Lambda probe `Hello*` vẫn chạy mỗi phút, `dotnet format` đầy đủ (lệch thứ tự `using`), 6 lỗ hổng high còn lại trong dependency lint/build. Mỗi việc nên là PR riêng nếu làm.
