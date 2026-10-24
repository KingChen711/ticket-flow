# Flash-sale full-stack load test

This NBomber executable runs **against the Compose stack only**. It creates an ephemeral
published event with 50 seats by default, then starts 300 one-shot virtual users through the
gateway: acquire a hold, create an order, and allow `PurchaseSaga` to complete it.

The VUs intentionally share one synthetic customer token so the run measures the seat/order
critical section instead of Keycloak token throughput. `docker-compose.e2e.yml` therefore raises
the per-identity hold caps only inside the isolated E2E project. Normal Compose and production
keep the fail-closed anti-abuse defaults; the Phase 12 hardening suite tests those defaults.

It deliberately does not run beside the kind cluster. Pause kind first, start Compose, then set
the two user passwords and internal-client secret environment variables (they are never
hard-coded in this project):

```powershell
$env:FLASH_SALE_CUSTOMER_PASSWORD = '<customer password>'
$env:FLASH_SALE_ORGANIZER_PASSWORD = '<organizer password>'
$env:FLASH_SALE_INTERNAL_CLIENT_SECRET = '<ticketflow-internal client secret>'
dotnet run --project tests/TicketFlow.FlashSale.LoadTests
```

Before starting Compose for this scenario, slow the unrelated reminder timer Lambda so it does not compete
with ticket PDF Lambda cold starts on this constrained local Docker host:

```powershell
$env:EVENT_REMINDER_SCHEDULE = 'rate(24 hours)'
docker compose up -d
Remove-Item Env:EVENT_REMINDER_SCHEDULE
```

Optional settings: `GATEWAY_BASE_URL`, `KEYCLOAK_BASE_URL`, `FLASH_SALE_SEAT_COUNT`,
`FLASH_SALE_VIRTUAL_USERS`, `FLASH_SALE_MAX_CONNECTIONS`,
`FLASH_SALE_SETTLE_TIMEOUT_SECONDS`, `FLASH_SALE_INTERNAL_CLIENT_ID`,
`FLASH_SALE_REPORT_DIR`, and the four datastore connections
`FLASH_SALE_RESERVATION_CONNECTION`, `FLASH_SALE_PAYMENT_CONNECTION`,
`FLASH_SALE_TICKETING_CONNECTION`, `FLASH_SALE_CLICKHOUSE_CONNECTION`. The default physical socket
pool is 64 even though NBomber activates 300 virtual users: Docker Desktop's Windows host-port
NAT can otherwise drop a large burst before it reaches the gateway. The default settlement
deadline is ten minutes because the scenario renders real ticket PDFs through LocalStack.
The default report location is the built project's `flash-sale-reports` directory (ignored by
Git). NBomber writes HTML/CSV/Markdown reports there alongside `flash-sale-summary.json`.

The process exits non-zero unless every expected seat is confirmed, no more than the provisioned
capacity is sold, all accepted orders settle, and no unexpected HTTP/transport failures occur.
Phase 12 additionally reads the four authoritative stores and requires the exact same `OrderId`
set in confirmed Orders, confirmed allocations, authorized attempts, issued tickets and ClickHouse
sales; ticket `SeatId` values must equal the confirmed allocation set. The JSON summary preserves
all five ID lists, so a mismatch is reproducible rather than hidden behind matching totals.
