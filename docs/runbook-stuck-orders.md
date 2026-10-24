# Runbook — đơn hàng kẹt, DLQ và redrive

> Phase 12 release evidence: Grafana now exposes payment reconciliation backlog,
> newly unknown/manual-review operations and saga deadline fires. CI provisions alerts
> `phase12-payment-unknown`, `phase12-saga-manual-review` and `phase12-saga-deadline`; the complete
> failure bundle and seed procedure is in [runbook-phase-12-release-gates.md](runbook-phase-12-release-gates.md).

Viết ở Phase 12 Commit 9. Dành cho người trực khi một đơn hàng ngừng chạy tiếp.

Nguyên tắc xuyên suốt: **không có nút "ép hoàn tất" và không có nút "ép huỷ".** Kết quả của một đơn phải đến từ chính các service đã làm việc đó. Mọi thao tác dưới đây chỉ làm một việc — bảo hệ thống **hỏi lại**.

---

## 1. Nhận biết

| Tín hiệu | Ý nghĩa |
|---|---|
| `ticketflow.saga.deadlines` tăng | Có participant im lặng quá `PurchaseSaga.ParticipantSilenceTimeout` (5 phút). Saga đang tự gửi lại lệnh. |
| `ticketflow.saga.manual_review` tăng | Saga đã thử `MaxDeadlineStrikes` (3) lần và **dừng lại**. Cần người xử lý. |
| `ticketflow.saga.missing_instance_redeliveries` tăng | Câu trả lời của participant về trước `OrderSubmitted`. Tự khỏi trong ~50 giây; tăng liên tục nghĩa là outbox checkout đang chậm. |
| `ticketflow.checkout.dispatch_failures` tăng | Relay outbox không đẩy được `OrderSubmitted` lên broker. Đơn đã bền vững, chỉ chưa khởi động saga. |
| `ticketflow.order.milestone_conflicts` tăng đều | Có writer thứ hai đang ghi vào order stream. Không mất dữ liệu (version check chặn), nhưng sai kiến trúc — điều tra. |
| Queue `*_error` có message | Fault vĩnh viễn hoặc đã hết bậc redelivery. Xem mục 4. |

**Đặt alert ở:** `ticketflow.saga.manual_review` (bất kỳ giá trị nào > 0 trong 5 phút) và độ sâu của mọi queue `*_error`. Hai cái này nghĩa là automation đã bó tay; các metric còn lại là cảnh báo sớm, tự phục hồi được.

---

## 2. Xem đơn nào đang kẹt

```bash
curl -sH "Authorization: Bearer $ADMIN_TOKEN" http://localhost:9002/ops/orders/stuck
```

Trả về mọi đơn đang ở ManualReview **hoặc** đã ăn ít nhất một strike — nhóm thứ hai là cảnh báo sớm của nhóm thứ nhất.

Xem một đơn cụ thể:

```bash
curl -sH "Authorization: Bearer $ADMIN_TOKEN" http://localhost:9002/ops/orders/<orderId>
```

Đọc theo thứ tự này để biết đơn đang chờ ai:

| Trường | Nếu null nghĩa là |
|---|---|
| `seatsConfirmedAt` | Reservation chưa xác nhận ghế |
| `paymentAuthorizedAt` | Payment chưa trả lời (xem mục 3 trước khi thử lại) |
| `ticketsIssuedAt` | Ticketing chưa phát vé |

`manualReviewReason` cho biết state nào đã hết lượt thử.

---

## 3. Trước khi thử lại một đơn kẹt ở thanh toán

**Kiểm tra `PaymentAttempt` trước.** Nếu nó ở trạng thái `Unknown`, tiền có thể đã bị trừ mà ta chưa biết — worker reconcile của Payment (Commit 6) đang hỏi lại nhà cung cấp. Chờ nó xong.

```sql
select state, failure_reason, reconcile_attempts, last_reconciled_at
from payment.payment_attempts where order_id = '<orderId>';
```

- `Authorized` → tiền đã trừ. Thử lại đơn là an toàn: saga sẽ đi tiếp sang phát vé.
- `Declined` → không có tiền nào bị trừ.
- `Unknown` và `reconcile_attempts` còn tăng → **chờ**, đừng đụng vào.
- `Unknown` và `reconcile_attempts` đứng yên ở mức trần → nhà cung cấp không trả lời được; phải đối soát thủ công với họ trước khi làm gì tiếp.

Đây là lý do runbook này không có bước "huỷ đơn kẹt": huỷ một đơn đang `Unknown` sẽ nhả ghế của một giao dịch có thể đã thanh toán.

---

## 4. Thao tác

### Đưa đơn trở lại automation

```bash
curl -X POST -H "Authorization: Bearer $ADMIN_TOKEN" \
  http://localhost:9002/ops/orders/<orderId>/resume
```

Xoá cờ manual review, reset strike, và **gửi lại đúng lệnh mà saga đang chờ**. Mọi lệnh participant đều idempotent (Commit 5–7) nên trường hợp xấu nhất là không có gì xảy ra. Đơn đã `Completed`/`Cancelled` sẽ trả `409` — cố ý.

### Redrive DLQ

Message trong `*_error` là fault vĩnh viễn hoặc đã hết bậc redelivery. Xem trước rồi mới đẩy lại:

```bash
# Xem nội dung
docker compose exec -T rabbitmq rabbitmqadmin get queue=<queue>_error count=5

# Đẩy lại vào queue chính
docker compose exec -T rabbitmq rabbitmqadmin publish \
  exchange=<queue> routing_key="" payload="<payload>"
```

Đọc header `MT-Fault-Message` trước. Nếu là `PermanentMessageFaultException` thì redrive **sẽ hỏng lại** — payload sai, không phải hạ tầng sai; phải sửa dữ liệu hoặc bỏ message.

### Sale không lên Kafka

`SaleRelayConsumer` chỉ ảnh hưởng số liệu phân tích, **không** ảnh hưởng đơn hàng — đơn vẫn `Confirmed` bình thường. Redrive từ DLQ của nó là an toàn: bản ghi claim chặn phát trùng, trừ đúng một khe hẹp (produce xong nhưng crash trước khi đánh dấu) có thể phát lại một lần. Nếu nghi ngờ, đối chiếu:

```sql
select id, claimed_at, relayed_at from mt_doc_salerelayrecord where id = '<orderId>';
```

`relayed_at` null nghĩa là chưa từng lên Kafka.

---

## 5. Những việc runbook này cố ý không cho làm

- **Không sửa tay `PurchaseState` trong Postgres.** Saga là writer duy nhất; sửa ngoài sẽ va với optimistic concurrency và có thể mất một transition.
- **Không ép một đơn thành `Confirmed`.** Ghế và vé chỉ có thật khi Reservation và Ticketing nói vậy.
- **Không huỷ đơn chỉ vì nó im lặng.** Đó chính là lỗi Commit 9 sinh ra để chặn: im lặng không phải là thất bại.
