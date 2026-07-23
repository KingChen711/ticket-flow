---
title: "Phase 12 Commit 1 — Failure-boundary baseline"
phase: 12
commit: 1
created: 2026-07-20
tags: [ticketflow, phase-12, hardening, expected-failure, testcontainers, failure-boundary]
---

# Phase 12 Commit 1 — Failure-boundary baseline

Tài liệu này là bằng chứng characterization **trước remediation**. Mục tiêu của gate đỏ không phải chứng minh test suite hỏng; nó khóa các production blocker đã quan sát để các commit sau chỉ được coi là xong khi chuyển đúng invariant liên quan sang xanh.

## Cách cô lập gate

- Project: `tests/TicketFlow.Phase12.HardeningTests/TicketFlow.Phase12.HardeningTests.csproj`.
- Project cố ý **chưa** nằm trong `TicketFlow.slnx`, vì Commit 1 yêu cầu regression cũ xanh đồng thời lưu test đỏ có chủ đích.
- Không test nào dùng `Skip`. Hai trait chạy tường minh:
  - `Phase12Harness`: harness/fake phải luôn xanh.
  - `Phase12ExpectedFailure`: invariant mong muốn; đỏ cho tới commit remediation tương ứng.
- Assembly tắt xUnit parallelism giữa test case để các permutation dùng chung real-container fixture có thứ tự ổn định; concurrency bên trong từng test vẫn được tạo bằng barrier có chủ đích.

## Harness thật và controlled external writes

Một collection fixture khởi động đúng image line đang dùng trong `docker-compose.infra.yml`:

| Dependency | Image | Bằng chứng trong test |
|---|---|---|
| Postgres | `postgres:16` | Marten store/query round-trip và EF check-in race trên database riêng |
| Redis | `redis:7` | Set/get round-trip qua `StackExchange.Redis` |
| RabbitMQ | `rabbitmq:3-management` | MassTransit consumer round-trip; `PurchaseSaga` thật persist bằng Marten |

`ControlledSideEffect<T>` tách `Attempts` khỏi `SuccessfulSideEffects`, hỗ trợ:

- rendezvous barrier để mở race deterministically;
- failure trước side effect;
- timeout bị chặn bởi cancellation token;
- side effect đã commit nhưng response bị mất (`AmbiguousSideEffectException`).

Adapter hiện có cho `IPaymentGatewayClient`, `ITicketPdfRenderer`; email dùng test-only fake vì Notification chưa có provider port (port thật thuộc Commit 13).

## Baseline đỏ đã tái hiện

| Nhóm invariant | Case | Baseline |
|---|---:|---|
| Checkout server-authoritative | hold A/order seat B; amount 0; USD/currency giả; Draft/Cancelled | 6 đỏ |
| Order save → publish | crash sau stream commit rồi retry tạo hai order | 1 đỏ |
| Reservation failure boundary | Redis confirm → DB fail; DB commit → SignalR fail | 2 đỏ |
| Reservation ownership/rebuild | stale order A unsell seat của B; DB inventory có nhưng Redis thiếu | 2 đỏ |
| Payment semantic duplicate | hai envelope khác `MessageId`, cùng `OrderId` | 1 đỏ: provider count `2`, mong đợi `1` |
| Ordering terminal duplicate | hai `OrderCompleted` đồng thời | 1 đỏ: append hai `OrderConfirmed` |
| Ticket artifact ambiguous outcome | upload PDF thành công, mất response, retry upload lại | 1 đỏ: object side effect `2`, mong đợi `1` |
| Saga milestone permutation/duplicate | mọi hoán vị của Seats → Payment → Tickets, mỗi fact gửi hai envelope | 5 đỏ, 1 control xanh (canonical order) |
| Catalog lifecycle permutation | mọi hoán vị Published/Rescheduled/Cancelled | 3 đỏ resurrect, 3 control xanh khi Cancelled thắng cuối |
| Ticket check-in concurrency | hai context cùng đọc `CheckedInAt = null` trước update | 1 đỏ: `Valid = 2`, mong đợi `1` |
| **Tổng** | **27 case** | **23 đỏ, 4 control xanh, 0 skip** |

Các control case xanh nằm chung theory với permutation đỏ để chứng minh harness không mặc định thất bại: canonical message order hiện tại hoàn tất được, và projection không resurrect khi cancellation tình cờ đến cuối.

## Lệnh và kết quả 2026-07-20

Build explicit gate:

```powershell
dotnet build tests\TicketFlow.Phase12.HardeningTests\TicketFlow.Phase12.HardeningTests.csproj --nologo --verbosity minimal
```

Kết quả: build thành công, không có warning mới từ hardening project. Hai `NU1510` còn lại phát ra từ `TicketFlow.Observability` đã tồn tại trước Commit 1.

Harness gate:

```powershell
dotnet test tests\TicketFlow.Phase12.HardeningTests\TicketFlow.Phase12.HardeningTests.csproj --no-build --nologo --verbosity minimal --filter "Category=Phase12Harness"
```

Kết quả: **5 passed, 0 failed, 0 skipped**.

Expected-failure gate:

```powershell
dotnet test tests\TicketFlow.Phase12.HardeningTests\TicketFlow.Phase12.HardeningTests.csproj --no-build --nologo --verbosity minimal --filter "Category=Phase12ExpectedFailure"
```

Kết quả mong đợi ở Commit 1: process exit code `1`; **23 failed, 4 passed, 0 skipped, 27 total**. Failure đều nằm ở assertion nghiệp vụ nêu trong ma trận trên, không còn lỗi container/migration/fixture setup.

Regression cũ được chạy trực tiếp theo bảy test project để tránh `dotnet test TicketFlow.slnx` phải build/scan toàn bộ service và load-test executable:

| Project | Passed |
|---|---:|
| Catalog Domain | 35 |
| Ordering Domain | 9 |
| Ordering Application | 14 |
| Payment Domain | 9 |
| Payment Infrastructure | 3 |
| Reservation Integration (Redis thật) | 5 |
| Ticketing Domain | 6 |
| **Tổng** | **81/81** |

## Promotion rule cho Commit 3 trở đi

1. Sửa production behavior trước, không đảo assertion để hợp thức hóa behavior cũ.
2. Chạy test invariant liên quan bằng `--filter FullyQualifiedName~...` cho tới khi xanh.
3. Giữ test executable, không đổi thành `Skip`; cập nhật ma trận baseline/evidence trong commit remediation.
4. Chỉ đưa hardening project vào solution/CI gate mặc định khi toàn bộ expected-failure case đã được promote hoặc CI có job filter tách rõ red-baseline khỏi regression.
5. Khi plan sai với code/probe thật, áp dụng nguyên tắc phản biện ở đầu `phase-12-plan.md`: ghi evidence/trade-off và cập nhật plan thay vì ép implementation theo giả định cũ.

## Trạng thái baseline sau Commit 11 (2026-07-23)

Baseline đỏ đã **đóng hoàn toàn**: `dotnet test --filter "Category=Phase12ExpectedFailure"` trả `No test matches`. Cả 23 blocker đỏ ban đầu đã được promote sang `Phase12Harness` (126/126 xanh), không case nào bị `Skip` hay xoá — bảng "Baseline đỏ đã tái hiện" ở trên giữ nguyên làm bằng chứng characterization *trước* remediation, không phải trạng thái hiện hành.

Điều đó thoả điều kiện của rule 4, nhưng việc đưa project vào `TicketFlow.slnx`/CI gate mặc định vẫn để cho **Commit 17** (nhóm F): nó cần Docker trong CI cùng với phần persistent-infra/CI của commit đó, và gộp sớm chỉ làm nhoè ranh giới regression 86/86 hiện tại.
