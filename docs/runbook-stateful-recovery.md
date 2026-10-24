# Runbook — Stateful recovery, migration và secret rotation

> The repeatable admission drill is `tests/ci/run-chaos.sh` on the disposable E2E Compose overlay.
> It force-recreates Redis, RabbitMQ, Kafka and Keycloak, verifies that each container ID changed,
> then checks broker-owned Redis/RabbitMQ/Kafka/Keycloak markers plus the exact
> order/seat/ticket/analytics IDs from the Playwright seed. See
> [Phase 12 release gates](runbook-phase-12-release-gates.md) for artifact collection. The manual
> procedures below remain the production-safe diagnostic path.

Runbook này đi cùng Phase 12 Commit 17. PostgreSQL là nguồn dữ liệu bền vững cho allocation;
Redis chỉ là cache/hold acceleration. RabbitMQ, Kafka và Keycloak vẫn phải giữ state qua restart
để tránh mất command, offset và user, nhưng không thành source of truth thay PostgreSQL.

## 1. Điều kiện trước khi cài

- Không dùng `:latest`. Chart mặc định pin image hạ tầng bằng digest; image ứng dụng phải dùng tag
  bất biến (`appImages.tag`, CI dùng Git SHA).
- Với profile production, tạo Secret tên `ticketflow-production-runtime` từ secret manager/CSI/
  ExternalSecret. Lấy danh sách key chuẩn từ `templates/secrets.yaml`; không copy các giá trị
  development. `service-client-secret` là bắt buộc cho client-credentials giữa Catalog/Ordering
  và các service nội bộ. Kafka production tối thiểu cần `kafka-username`, `kafka-password` và
  `kafka-client-properties`; key cuối là file properties dùng bởi provisioning/readiness CLI
  (`security.protocol=SASL_SSL`, SCRAM mechanism/JAAS và CA path nếu broker dùng private CA).
  Application đọc credential từ hai key đầu, không nhúng secret vào values/rendered manifest.
  RabbitMQ dùng identity riêng cho từng consumer: `rabbitmq-catalog-password`,
  `rabbitmq-reservation-password`, `rabbitmq-ordering-password`,
  `rabbitmq-notification-password`, `rabbitmq-search-password`, `rabbitmq-payment-password`,
  `rabbitmq-ticketing-password`, `rabbitmq-recommendation-password` và
  `rabbitmq-analytics-password`. Không dùng lại admin credential hoặc một app credential chung.
  Trên managed broker/PVC cũ, thu hồi principal `ticketflow_app` sau rollout; chart development
  tự rotate password của principal legacy này và đặt cả ba permission thành deny-all (`^$`).
- Realm development không được import ở production. `values-production.yaml` cố ý không chứa
  password/token và sẽ fail render nếu bật lại `secrets.create` hoặc endpoint HTTP. Realm ngoài
  cluster phải đăng ký chính xác callback
  `https://ticketflow.example/organizer/api/auth/callback/keycloak` và
  `https://ticketflow.example/scanner/api/auth/callback/keycloak` cho hai client tương ứng
  (thay hostname theo release); callback root `/api/auth/*` cũ không còn hợp lệ.
- Functions là external dependency có tên DNS rõ ràng (`functions-provider`) và health gate. Với
  kind, provider phải chạy trên network Docker `kind` dưới DNS đã cấu hình; với production, trỏ
  `runtime.functions.externalName/serviceUrl/healthUrl` tới provider HTTPS thật. Không dùng
  `host.docker.internal`.

Kiểm tra trước deploy:

```bash
helm lint infrastructure/k8s/ticketflow
helm template ticketflow infrastructure/k8s/ticketflow \
  -f infrastructure/k8s/ticketflow/values-production.yaml >/tmp/ticketflow-production.yaml
grep -E 'image:.*:latest|host\.docker\.internal|start-dev' /tmp/ticketflow-production.yaml && exit 1 || true
# Khi install/upgrade thật, chờ cả workload lẫn provisioning/migration Jobs.
helm upgrade --install ticketflow infrastructure/k8s/ticketflow \
  -f infrastructure/k8s/ticketflow/values-production.yaml \
  -n ticketflow --create-namespace --wait --wait-for-jobs --timeout 15m
```

## 2. Migration và provisioning order

`ticketflow-database-bootstrap` tạo database + role theo kiểu idempotent. Mỗi migration Job chạy
chính application image với `--migrate`; EF/Marten giữ PostgreSQL advisory lock theo component.
Pod ứng dụng có init container kiểm tra đúng migration/index/table trước khi Kestrel hoặc consumer
được khởi động. Không scale Deployment để “chạy migration nhanh hơn”.

```bash
kubectl -n ticketflow get jobs
kubectl -n ticketflow logs -l app=database-bootstrap --tail=200
kubectl -n ticketflow logs -l app=catalog-migration --tail=200
kubectl -n ticketflow get pods -o custom-columns=NAME:.metadata.name,INIT:.status.initContainerStatuses[*].state
```

Kafka topic do `ticketflow-kafka-provision` tạo/cập nhật trước khi Ordering/Analytics qua init gate.
Job chỉ tăng partition; nếu cluster đang có nhiều partition hơn desired, Job fail để operator quyết
định, không shrink phá dữ liệu.

```bash
kubectl -n ticketflow logs -l app=kafka-provision --tail=200
kubectl -n ticketflow exec kafka-0 -- /opt/kafka/bin/kafka-topics.sh \
  --bootstrap-server kafka:9092 --topic sale-recorded --describe
kubectl -n ticketflow exec kafka-0 -- /opt/kafka/bin/kafka-configs.sh \
  --bootstrap-server kafka:9092 --entity-type topics --entity-name sale-recorded --describe
```

## 3. Restart drill không mất state

Chạy một lần cho từng component, không xóa PVC:

```bash
kubectl -n ticketflow delete pod redis-0
kubectl -n ticketflow delete pod rabbitmq-0
kubectl -n ticketflow delete pod kafka-0
kubectl -n ticketflow delete pod keycloak-0
kubectl -n ticketflow rollout status statefulset/redis --timeout=5m
kubectl -n ticketflow rollout status statefulset/rabbitmq --timeout=5m
kubectl -n ticketflow rollout status statefulset/kafka --timeout=5m
kubectl -n ticketflow rollout status statefulset/keycloak --timeout=5m
```

Sau mỗi restart:

- Redis: `redis-cli -a "$REDIS_PASSWORD" INFO persistence` phải có `aof_enabled:1` và
  `aof_last_write_status:ok`; Reservation `/health/ready` trở lại 200 rồi reconciler phải hội tụ.
- RabbitMQ: `rabbitmqctl list_queues name durable type messages_ready messages_unacknowledged`;
  queue business phải `durable=true`, `type=quorum`, backlog không biến mất. Kiểm tra thêm
  `rabbitmqctl list_permissions -p ticketflow`: mỗi service chỉ được truy cập endpoint prefix của
  nó, contract exchange `TicketFlow.*` và temporary bus/system exchange cần cho MassTransit.
- Kafka: topic vẫn 6 partition, `retention.ms=604800000`; consumer-group offset không quay về đầu.
- Keycloak: login/user tạo trước restart vẫn tồn tại vì Keycloak dùng database
  `ticketflow_keycloak` trên PostgreSQL, không dùng H2 trong container.
- Chạy invariant query Phase 12: một active allocation/seat; một authorized payment, issuance và
  completion/order. Sai lệch chỉ được nằm trong `Unknown`/`ManualReview` có deadline.

## 4. Redis mất/corrupt cache — rebuild an toàn

Không restore Redis bằng cách đoán owner và không `FLUSHDB` khi Reservation còn nhận traffic.

1. Scale Reservation về 0 hoặc đóng admission/checkout; giữ Ordering/consumer mới không gửi thêm
   mutation.
2. Chụp bằng chứng: log reconciler, `INFO persistence`, danh sách PVC và invariant query Postgres.
3. Nếu AOF còn hợp lệ, restart pod và để Redis tự replay. Không xóa PVC.
4. Nếu AOF hỏng và buộc thay PVC, tạo PVC mới theo quy trình change-management; tuyệt đối không
   xóa PVC cũ trước khi có snapshot/backup. Redis rỗng chỉ là cache rỗng, không phải ghế available.
5. Start đúng một Reservation replica với `Reservation__CacheReconcileMode=RepairCache` (hoặc
   `QuarantineConflict` khi đã có conflict). Reconciler dựng owner + generation từ
   `seat_allocations` PostgreSQL.
6. Chỉ mở traffic khi remaining drift bằng 0. Owner conflict phải quarantine/manual review, không
   tự chọn bên thắng.

## 5. Secret rotation

External secret controller cập nhật Secret; sau đó rollout từng consumer để connection mới được
tạo. Với database/broker, tạo credential mới trước, rollout và quan sát readiness, rồi mới thu hồi
credential cũ. Không đổi password PostgreSQL đang nằm trong PVC chỉ bằng cách sửa
`POSTGRES_PASSWORD`: biến này chỉ có tác dụng lúc init database lần đầu.

```bash
kubectl -n ticketflow rollout restart deployment/catalog deployment/identity deployment/reservation
kubectl -n ticketflow rollout status deployment/catalog --timeout=5m
```

RabbitMQ runtime user không có tag administrator; admin credential chỉ dành cho operator.
Production phải dùng secret manager và audit rotation. Chart development tạo secret cục bộ chỉ để
kind chạy từ số 0.

## 6. Giới hạn đã chấp nhận

PVC trên kind một node chứng minh durability/recovery sau pod recreation, không chứng minh HA,
node-loss hay multi-zone failover. Production dùng managed Postgres/Redis/RabbitMQ/Kafka/Keycloak
hoặc operator có replication/backup; `values-production.yaml` tắt các single-node embedded instance
và giữ nguyên migration/readiness contract cho endpoint managed.
