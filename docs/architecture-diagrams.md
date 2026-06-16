# Architecture diagrams — current state

Snapshot of the **as-built** system on 2026-06-16. Two views:

1. **Infrastructure** — what actually runs (containers, ports, volumes, networks) per [docker-compose.yml](../docker-compose.yml).
2. **Application** — bounded contexts, sync vs async edges, and the choreography saga per [docs/architecture.md](architecture.md).

Both diagrams are Mermaid. They render natively on GitHub and in VS Code's Markdown preview.

---

## 1. Infrastructure view

Single Docker network `saga` (bridge). One Postgres container hosts four logical DBs (`orders`, `payments`, `inventory`, `shipping`) created by [build/postgres/init.sql](../build/postgres/init.sql). All telemetry is funnelled through the OTel Collector before fanning out to Jaeger and Prometheus.

```mermaid
flowchart TB
    subgraph host["Developer host (Windows / Docker Desktop)"]
        direction TB
        browser[["Browser<br/>localhost"]]:::ext

        subgraph net["docker network: saga (bridge)"]
            direction TB

            subgraph apps["Application containers"]
                direction LR
                webui["saga-webui<br/>WebUI :8080<br/>host :5000"]:::app
                order["saga-order<br/>OrderService :8080<br/>host :5001"]:::app
                payment["saga-payment<br/>PaymentService :8080<br/>host :5002"]:::app
                inventory["saga-inventory<br/>InventoryService :8080<br/>host :5003"]:::app
                shipping["saga-shipping<br/>ShippingService :8080<br/>host :5004"]:::app
            end

            subgraph data["Data plane"]
                direction LR
                rabbit["saga-rabbitmq<br/>RabbitMQ 3.13-mgmt<br/>:5672 AMQP / :15672 UI"]:::infra
                pg["saga-postgres<br/>Postgres 16<br/>:5432<br/>DBs: orders, payments,<br/>inventory, shipping"]:::infra
                pgvol[("pgdata<br/>volume")]:::vol
            end

            subgraph obs["Observability plane"]
                direction LR
                otel["saga-otelcol<br/>OTel Collector<br/>:4317 gRPC / :4318 HTTP<br/>:8889 internal /metrics"]:::obs
                jaeger["saga-jaeger<br/>Jaeger all-in-one<br/>:16686 UI"]:::obs
                prom["saga-prometheus<br/>Prometheus<br/>:9090"]:::obs
                graf["saga-grafana<br/>Grafana 11<br/>:3000"]:::obs
            end
        end
    end

    %% User-facing
    browser -- ":5000" --> webui
    browser -. ":15672" .-> rabbit
    browser -. ":16686" .-> jaeger
    browser -. ":9090" .-> prom
    browser -. ":3000" .-> graf

    %% Sync HTTP (WebUI is the only sync caller)
    webui -- "HTTP + Polly<br/>StandardResilienceHandler" --> order
    webui -. read-only HTTP .-> payment
    webui -. read-only HTTP .-> inventory
    webui -. read-only HTTP .-> shipping

    %% Async messaging
    order  <-- "AMQP" --> rabbit
    payment <-- "AMQP" --> rabbit
    inventory <-- "AMQP" --> rabbit
    shipping <-- "AMQP" --> rabbit

    %% Persistence
    order -- "EF Core<br/>db=orders" --> pg
    payment -- "EF Core<br/>db=payments" --> pg
    inventory -- "EF Core<br/>db=inventory" --> pg
    shipping -- "EF Core<br/>db=shipping" --> pg
    pg --- pgvol

    %% Telemetry (all OTLP into the collector)
    order -- "OTLP gRPC :4317<br/>traces + metrics" --> otel
    payment -- OTLP --> otel
    inventory -- OTLP --> otel
    shipping -- OTLP --> otel
    webui -- OTLP --> otel

    otel -- "traces" --> jaeger
    prom -- "scrape /metrics" --> otel
    prom -. "scrape /metrics<br/>(if service-side<br/>Prometheus exporter)" .-> apps
    graf -- "datasource: Prometheus" --> prom
    graf -. "datasource: Jaeger" .-> jaeger

    classDef app    fill:#dbeafe,stroke:#1e40af,color:#0b2447
    classDef infra  fill:#fef3c7,stroke:#b45309,color:#3f2406
    classDef obs    fill:#dcfce7,stroke:#166534,color:#052e16
    classDef vol    fill:#f5f5f4,stroke:#57534e,color:#1c1917
    classDef ext    fill:#fae8ff,stroke:#86198f,color:#3b0764
```

### Container roster

| Container | Image | Host port → container | Role |
|---|---|---|---|
| `saga-webui` | built from `build/docker/Dockerfile.dotnet` (`SERVICE_NAME=WebUI`) | 5000 → 8080 | Blazor Server UI; only sync HTTP client |
| `saga-order` | …`SERVICE_NAME=OrderService` | 5001 → 8080 | Order aggregate, saga timeout watchdog, stage tracker |
| `saga-payment` | …`SERVICE_NAME=PaymentService` | 5002 → 8080 | Payment aggregate, charge / refund |
| `saga-inventory` | …`SERVICE_NAME=InventoryService` | 5003 → 8080 | Reservation aggregate, stock guard |
| `saga-shipping` | …`SERVICE_NAME=ShippingService` | 5004 → 8080 | Shipment aggregate |
| `saga-rabbitmq` | `rabbitmq:3.13-management` | 5672 / 15672 | Message broker + management UI |
| `saga-postgres` | `postgres:16` | 5432 | Per-service logical DBs (`pgdata` volume) |
| `saga-jaeger` | `jaegertracing/all-in-one:1.60` | 16686 / 14268 | Trace storage + UI |
| `saga-otelcol` | `otel/opentelemetry-collector-contrib:0.108.0` | 4317 / 4318 / 8889 | Single OTLP ingress; fans out to Jaeger + Prometheus |
| `saga-prometheus` | `prom/prometheus:v2.54.1` | 9090 | Metric scraping + storage |
| `saga-grafana` | `grafana/grafana:11.2.0` | 3000 | Dashboards (provisioned from [build/observability/grafana](../build/observability/grafana)) |

> **Note — what is *not* yet here:** the GitOps overlay (Kubernetes Deployments, Istio CRDs, ArgoCD Applications, kube-prometheus-stack) lives in the sibling `gitops/` repo and is not yet applied. The current diagram represents the local docker-compose deployment only.

---

## 2. Application view

Choreography saga over RabbitMQ. Solid arrows are forward steps; dashed are compensations / failure paths. Every aggregate owns its state machine; every consumer is idempotent (MassTransit inbox + per-aggregate status guards + `[ConcurrencyCheck]`).

```mermaid
flowchart LR
    UI["WebUI<br/>(Blazor Server)<br/>Polly StandardResilienceHandler<br/>+ X-Correlation-ID middleware"]:::ui

    subgraph saga["Choreography saga (RabbitMQ + MassTransit outbox/inbox)"]
        direction LR
        O["order-service<br/>Order aggregate<br/>Stage state-machine<br/>OrderTimeoutWatchdog"]:::svc
        I["inventory-service<br/>Reservation +<br/>ProductStock"]:::svc
        P["payment-service<br/>Payment aggregate"]:::svc
        S["shipping-service<br/>Shipment aggregate"]:::svc
    end

    DBO[("orders DB")]:::db
    DBI[("inventory DB")]:::db
    DBP[("payments DB")]:::db
    DBS[("shipping DB")]:::db

    UI -- "POST /orders" --> O
    UI -. "GET (read-only)" .-> P
    UI -. "GET (read-only)" .-> I
    UI -. "GET (read-only)" .-> S

    %% Forward (reserve-then-charge)
    O -- "OrderPlaced" --> I
    I -- "InventoryReserved" --> P
    P -- "PaymentSucceeded" --> S
    S -- "ShipmentDispatched" --> O

    %% Stage trackers (order-service consumes its own saga events
    %% to advance Order.Stage)
    I -. "InventoryReserved" .-> O
    P -. "PaymentSucceeded" .-> O

    %% Compensations
    P -. "PaymentFailed" .-> O
    P -. "PaymentFailed" .-> I
    I -. "InventoryUnavailable" .-> O
    I -. "InventoryUnavailable" .-> P
    P -. "PaymentRefunded" .-> I

    %% Persistence (one DB per service)
    O --- DBO
    I --- DBI
    P --- DBP
    S --- DBS

    classDef svc fill:#dbeafe,stroke:#1e40af,color:#0b2447
    classDef ui  fill:#fde68a,stroke:#b45309,color:#3f2406
    classDef db  fill:#f1f5f9,stroke:#334155,color:#0f172a
```

### Saga sequence (happy path + the three failure paths)

```mermaid
sequenceDiagram
    autonumber
    participant UI as WebUI
    participant O as OrderService
    participant I as InventoryService
    participant P as PaymentService
    participant S as ShippingService
    participant W as OrderTimeoutWatchdog<br/>(in OrderService)

    UI->>O: POST /orders {items}
    O-->>UI: 202 Accepted (orderId, correlationId)
    O-)I: OrderPlaced

    alt Happy path
        I-)P: InventoryReserved
        I-)O: InventoryReserved (stage tracker)
        P-)S: PaymentSucceeded
        P-)O: PaymentSucceeded (stage tracker)
        S-)O: ShipmentDispatched
        Note over O: Order.Complete()
    else Inventory shortage (OUT_OF_STOCK_*)
        I-)O: InventoryUnavailable
        I-)P: InventoryUnavailable
        Note over O: Order.Cancel("inventory_unavailable")
        Note over P: nothing to refund
    else Payment decline (FAIL_PAY* / amount > 100k)
        I-)P: InventoryReserved
        P-)O: PaymentFailed
        P-)I: PaymentFailed
        Note over I: Reservation.Release()
        Note over O: Order.Cancel("payment_failed:*")
    else Saga timeout (STALL_*)
        I-)P: InventoryReserved
        Note over P: 5-min stall (sentinel)
        W->>O: scan Pending past Saga:Timeout:Total
        W-)I: synthetic PaymentFailed("saga_timeout")<br/>OR synthetic InventoryUnavailable("(timeout)")
        Note over I,P: same compensations as<br/>the corresponding failure path
    end
```

### Event catalogue

Records live in [src/Shared/Saga.Shared.Contracts/Events.cs](../src/Shared/Saga.Shared.Contracts/Events.cs); all implement `ICorrelatedEvent`.

| Event | Producer | Consumers | Kind |
|---|---|---|---|
| `OrderPlaced` | order | inventory | forward |
| `InventoryReserved` | inventory | payment, order (stage) | forward |
| `PaymentSucceeded` | payment | shipping, order (stage) | forward |
| `ShipmentDispatched` | shipping | order | forward |
| `OrderCompleted` | order | (terminal) | forward |
| `InventoryUnavailable` | inventory | order, payment | failure |
| `PaymentFailed` | payment | order, inventory | failure |
| `InventoryReleased` | inventory | (audit) | compensation |
| `PaymentRefunded` | payment | inventory | compensation |
| `OrderCancelled` | order | (terminal) | compensation |

### Cross-cutting concerns (per service)

| Concern | Implementation |
|---|---|
| Atomic publish | EF Core transactional outbox via MassTransit (`AddEntityFrameworkOutbox`) |
| Idempotent consume | MassTransit inbox (`AddInboxStateEntity`) + aggregate status guards + `[ConcurrencyCheck]` on saga-relevant fields |
| Retries | MassTransit `UseMessageRetry` (5 attempts, 200 ms → 10 s exponential); exhausted → `<queue>_error` DLQ |
| HTTP resilience | WebUI only — `AddStandardResilienceHandler()` (Polly v8: retry + circuit breaker + timeout + concurrency limiter) |
| Correlation | `X-Correlation-ID` middleware → `Activity` baggage → Serilog `LogContext` → MassTransit headers |
| Health | `/healthz/live` (process), `/healthz/ready` (Postgres + RabbitMQ probes) |
| Telemetry | OTel SDK auto + `Saga.Choreography` ActivitySource; OTLP → Collector |

---

## How to regenerate / verify

- Render the diagrams: open this file in VS Code with the Markdown Preview, or push to GitHub.
- Verify the infrastructure diagram matches reality:

  ```powershell
  docker compose ps
  docker compose config --services
  ```

- Verify the application diagram matches reality: events declared in [Events.cs](../src/Shared/Saga.Shared.Contracts/Events.cs), consumer-to-event bindings in each `*/Consumers/*Consumers.cs`, saga assertions in [tests/Saga.IntegrationTests/SagaSmokeTests.cs](../tests/Saga.IntegrationTests/SagaSmokeTests.cs).
