# Saga — App Repo

E-commerce choreography saga implemented in .NET 8 with MassTransit + RabbitMQ + EF Core + OpenTelemetry. Four services participate in a distributed transaction with explicit forward and compensating handlers.

```
order-service ──OrderPlaced──▶ payment-service ──PaymentSucceeded──▶ inventory-service ──InventoryReserved──▶ shipping-service ──ShipmentDispatched──▶ order-service (Completed)
                              │
                              └─ PaymentFailed ─▶ order-service (Cancelled)
                                                   inventory-service ─ InventoryUnavailable ─▶ payment-service (Refund) + order-service (Cancel)
```

## Layout

| Path | Purpose |
|---|---|
| `src/Shared/Saga.Shared.Contracts` | Event records (`OrderPlaced`, `PaymentSucceeded`, …). All implement `ICorrelatedEvent`. |
| `src/Shared/Saga.Shared.Infrastructure` | OTel wiring, MassTransit + EF outbox config, Serilog, correlation-id middleware, health checks. |
| `src/OrderService` (5001) | Owns Order. Publishes `OrderPlaced`. Consumes `PaymentFailed`, `InventoryUnavailable`, `ShipmentDispatched`. |
| `src/PaymentService` (5002) | Charges card. Publishes `PaymentSucceeded` / `PaymentFailed`. Refunds on `InventoryUnavailable`. |
| `src/InventoryService` (5003) | Reserves stock. Publishes `InventoryReserved` / `InventoryUnavailable`. Releases on `PaymentFailed`. |
| `src/ShippingService` (5004) | Creates shipment + tracking number. Publishes `ShipmentDispatched`. |
| `src/WebUI` (5000) | Blazor Server UI for placing orders, watching the saga live, and inspecting state across all services. |
| `tests/Saga.Shared.Contracts.Tests` | Contract tests asserting every event is correlated. |
| `tests/Saga.IntegrationTests` | Testcontainers + WebApplicationFactory skeleton (TODO). |
| `build/docker/Dockerfile.dotnet` | Shared multi-stage Dockerfile, parameterised by `SERVICE_NAME`. |
| `build/observability/` | OTel collector, Prometheus, Grafana provisioning. |
| `build/postgres/init.sql` | Creates `orders`, `payments`, `inventory`, `shipping` databases. |
| `docker-compose.yml` | Local platform + all 4 services. |

## Design notes

- **Choreography only.** No central orchestrator. Each service owns its forward step and its compensating handler.
- **Event-carried state transfer.** `PaymentSucceeded` carries the order's items so `inventory-service` does not need to query `order-service` and does not need to cache `OrderPlaced` data. This eliminates cross-event ordering dependencies inside a single consumer.
- **Transactional outbox + inbox.** Every state-changing consumer uses MassTransit's `AddEntityFrameworkOutbox` so the DB write and the published event commit atomically; the inbox dedupes by message id.
- **Resilience.** In-process retry (exponential, 5 attempts) and dead-letter via RabbitMQ `_error` queues for poison messages. Long-tail delayed redelivery is intentionally not wired (would require the RabbitMQ delayed-exchange plugin or an external scheduler).
- **Idempotency.** Every consumer guards by domain status / unique index in addition to the inbox.
- **Correlation.** `ICorrelatedEvent` extends `MassTransit.ICorrelatedBy<Guid>` so MassTransit propagates the saga's correlation id end-to-end (inbox + Jaeger traces).
- **Health.** `/healthz/live` is process-only; `/healthz/ready` checks Postgres + RabbitMQ.

## Run locally (Docker — recommended)

Prereq: Docker Desktop with Linux containers.

```powershell
cd C:\Project\app
docker compose up --build -d
```

Endpoints:

- Order API: <http://localhost:5001>
- Payment API: <http://localhost:5002>
- Inventory API: <http://localhost:5003>
- Shipping API: <http://localhost:5004>
- RabbitMQ UI: <http://localhost:15672> (saga / saga)
- Jaeger UI: <http://localhost:16686>
- Prometheus: <http://localhost:9090>
- Grafana: <http://localhost:3000> (admin / admin, anonymous viewer enabled)
- **Web UI: <http://localhost:5000>** — start here## Trigger the saga

Happy path:

```powershell
$body = @{
  customerId = [guid]::NewGuid()
  items = @(
    @{ sku = "SKU-001"; quantity = 2; unitPrice = 19.99 },
    @{ sku = "SKU-002"; quantity = 1; unitPrice = 49.50 }
  )
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Uri http://localhost:5001/orders -Method POST -ContentType application/json -Body $body
```

Failure injectors (built into the demo logic):

| Trigger | What happens |
|---|---|
| Any item SKU starts with `FAIL_PAY` | `PaymentService` publishes `PaymentFailed` → order cancelled, no inventory reserved. |
| Total amount > 100 000 | Same as above (treated as fraud-decline). |
| Any item SKU starts with `OUT_OF_STOCK` | `PaymentService` succeeds, `InventoryService` publishes `InventoryUnavailable` → payment refunded, order cancelled. |

Inspect:

- Trace: Jaeger UI → service `order-service` → click any trace → see hops across services and broker.
- Final order state: `GET http://localhost:5001/orders/{id}`.
- RabbitMQ queues: <http://localhost:15672/#/queues>.

## Run locally (without Docker)

Requires .NET 8 SDK and a reachable RabbitMQ + Postgres. Bring up just the platform:

```powershell
docker compose up -d rabbitmq postgres jaeger otel-collector prometheus grafana
```

Then in four terminals (one per service):

```powershell
$env:ConnectionStrings__Postgres = "Host=localhost;Database=orders;Username=saga;Password=saga"
$env:RabbitMq__Host = "localhost"
$env:RabbitMq__Username = "saga"
$env:RabbitMq__Password = "saga"
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"
dotnet run --project src/OrderService/Saga.OrderService.csproj
```

Repeat with database name `payments`/`inventory`/`shipping` for the other three.

## Known environment quirk on this workstation

`dotnet build` of the solution intermittently fails with `UnauthorizedAccessException` reading `Saga.Shared.Contracts.dll`. Cause: corporate EDR / Defender real-time scan locks freshly-written DLLs. Workarounds:

- Build inside Docker (`docker compose build`), which runs the compiler on Linux and is unaffected.
- Or ask IT to add a Defender exclusion for `C:\Project\app\**\bin` and `C:\Project\app\**\obj`.
- Or build to `%TEMP%` (`-p:BaseOutputPath=...`) — but per-project intermediate paths must stay separate.

## Tests

```powershell
dotnet test tests/Saga.Shared.Contracts.Tests/Saga.Shared.Contracts.Tests.csproj
```

The integration test project is a placeholder (Testcontainers + `WebApplicationFactory`) marked `[Fact(Skip="TODO")]`. Wire-up is the next milestone.

## What this repo does NOT contain

- Kubernetes manifests, Helm charts, Istio CRDs, ArgoCD apps → those live in the **`gitops/`** repo.
- Production secrets — `saga/saga` everywhere is dev-only.
- CI workflows yet — coming under `.github/workflows/` (build, test, image push, manifest bump).
