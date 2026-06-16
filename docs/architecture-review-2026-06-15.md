# Architecture review — 2026-06-15

> Solution-architect review of `app/` against the original project goals stated in the brief:
>
> 1. Event-Driven Architecture using a message broker, combined with the Saga Pattern for distributed transaction management.
> 2. Additional resilience patterns (e.g. Circuit Breaker) for fault tolerance.
> 3. Compensating transactions for data consistency at every saga stage.
> 4. A fully functional microservices platform that maintains eventual consistency without traditional ACID transactions.
> 5. Validation of common failure scenarios (payment failure, inventory shortage, service timeout) via automated rollback and compensation.
>
> Outcome of this review: **all five goals are met within `app/` scope.** A short list of *recommended* enhancements follows.

## Coverage matrix

| # | Goal / capability | Status | Evidence |
|---|---|---|---|
| 1 | EDA on a real broker | ✅ Done | RabbitMQ + MassTransit; per-type fan-out exchange + per-consumer durable queue. [docker-compose.yml](../docker-compose.yml), [MassTransitExtensions.cs](../src/Shared/Saga.Shared.Infrastructure/MassTransitExtensions.cs). |
| 1 | Saga pattern (choreography) | ✅ Done | All 4 services emit/consume events; no orchestrator. [Events.cs](../src/Shared/Saga.Shared.Contracts/Events.cs), per-service `Consumers/`. |
| 1 | Idempotent consumers | ✅ Done | MassTransit inbox (`AddInboxStateEntity`) + status guards + unique indexes on `OrderId`. |
| 1 | Transactional outbox | ✅ Done | `AddEntityFrameworkOutbox<TDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); ... })`. |
| 1 | Correlation propagation | ✅ Done | `ICorrelatedEvent.CorrelationId` (MassTransit picks it up by convention) + `X-Correlation-ID` HTTP middleware + Activity tag + Serilog enrichment. |
| 2 | **Circuit Breaker** | ✅ Done (where it applies) | `AddStandardResilienceHandler()` on every WebUI → API typed client (Polly v8 pipeline = retry + circuit breaker + total/attempt timeout + concurrency limiter). [WebUI/Program.cs](../src/WebUI/Program.cs). Deliberately **not** wired between saga services because the saga is async-only over RabbitMQ. |
| 2 | Retry with exponential backoff | ✅ Done | Two layers: Polly retry on outbound HTTP, MassTransit `UseMessageRetry` (200 ms → 10 s, 5 attempts) on consumers. |
| 2 | Timeout | ✅ Done | Polly `StandardResilienceHandler` total + per-attempt timeouts on HTTP; MassTransit consumer cancellation tokens propagated through `db.SaveChangesAsync(ct)` and `ctx.Publish(..., ct)`. |
| 2 | Bulkhead / concurrency limit | ✅ Done | Polly `StandardResilienceHandler` includes a concurrency limiter (rate limiter) by default. MassTransit prefetch + `ConcurrentMessageLimit` left at framework defaults. |
| 2 | Dead-letter queue | ✅ Done | RabbitMQ `<queue>_error` (MassTransit default) — visible in <http://localhost:15672>. |
| 3 | Compensations defined for every forward step | ✅ Done | See [architecture.md §4](architecture.md#4-compensation-map). |
| 3 | Compensations are idempotent | ✅ Done | `Payment.Status != Succeeded` ⇒ refund no-op; `Reservation.Status == Released` ⇒ release no-op. |
| 3 | Compensation tests force the failure | ✅ Done | 3 of 4 tests in [SagaSmokeTests.cs](../tests/Saga.IntegrationTests/SagaSmokeTests.cs) drive a compensation. |
| 4 | Eventual consistency without 2PC | ✅ Done | One DB per service, no XA/2PC, saga + outbox are the only correctness mechanisms. |
| 4 | One DB per service | ✅ Done | `orders` / `payments` / `inventory` / `shipping` databases on a shared Postgres host (logically separated, no cross-DB queries). |
| 4 | Health probes | ✅ Done | `/healthz/live` (process), `/healthz/ready` (Postgres + RabbitMQ). [HostingExtensions.cs](../src/Shared/Saga.Shared.Infrastructure/HostingExtensions.cs). |
| 4 | EF migrations | ✅ Done | `MigrateAsync()` at startup; per-service `Migrations/` folders. |
| 5 | Payment failure scenario | ✅ Done | `PaymentFailed_results_in_OrderCancelled` integration test; sentinels `FAIL_PAY*` SKU + `total > 100_000`. |
| 5 | Inventory shortage scenario | ✅ Done | `InventoryUnavailable_triggers_PaymentRefunded_and_OrderCancelled`; sentinel `OUT_OF_STOCK*` SKU. |
| 5 | Service timeout scenario | ✅ Done | `Timeout_triggers_compensation`; sentinel `STALL_*` SKU stalls PaymentService 5 min, watchdog (overridden to 8 s in test fixture) emits synthetic `PaymentFailed(reason: "saga_timeout")`. |
| 5 | Final-state assertions | ✅ Done | Each test asserts the eventual state of every service's read model, not just the published event. |

## Observability — also reviewed

| Signal | Status | Note |
|---|---|---|
| Distributed tracing (Jaeger) | ✅ Done | OTLP → OTel Collector → Jaeger. ASP.NET Core, HttpClient, EF Core, MassTransit, custom `Saga.Choreography` ActivitySource. |
| Metrics (Prometheus) | ✅ Done | Per-service `/metrics`; Grafana dashboard with RED + saga forward vs compensation rates. |
| Structured logs | ✅ Done | Serilog JSON, enriched with `service`, `correlationId`. |
| RabbitMQ queue depth panel | ⚠️ Pending | Placeholder in dashboard; needs `rabbitmq_exporter` (gitops repo). |

## Recommended enhancements (optional, not gating)

These would polish the platform without changing its architecture. Listed in priority order; each is a follow-up, not a blocker.

1. ✅ **Saga business metric** — *Done.* `saga.terminal{outcome=completed|cancelled, reason=...}` `Counter<long>` on the `Saga.Choreography` meter ([SagaMetrics.cs](../src/Shared/Saga.Shared.Infrastructure/SagaMetrics.cs)), incremented by OrderService at every terminal transition. Grafana panels "Saga terminal outcomes" + "Compensation ratio" wired in [saga-overview.json](../build/observability/grafana/provisioning/dashboards/saga-overview.json).
2. **Tune MassTransit concurrency** — default prefetch / `ConcurrentMessageLimit` is fine for a demo but production should set explicit per-endpoint values; currently implicit.
3. **Delayed redelivery for long-tail faults** — wire MassTransit `UseDelayedRedelivery` (1 m / 5 m / 15 m) with the RabbitMQ delayed-message-exchange plugin if the demo evolves toward production. Today's retry + DLQ is sufficient for the demo's threat model and intentionally omitted to avoid the plugin dependency.
4. ✅ **CI workflows** — *Done.* [`ci.yml`](../.github/workflows/ci.yml) runs build + unit tests + integration tests (docker compose) + image build & push (GHCR, matrix x5) + Trivy scan with SARIF upload. [`bump-gitops.yml`](../.github/workflows/bump-gitops.yml) chains on a successful main run and opens a PR against the gitops repo (gracefully skips if the gitops repo isn't yet bootstrapped).
5. **Schema versioning for events** — current contracts are V1; add namespacing (e.g. `Saga.Shared.Contracts.V1`) before any breaking change.
6. **Authentication / authorization** — out of scope today (demo). Production would terminate AuthN/AuthZ at the edge (Istio AuthorizationPolicy) and propagate identity in headers.
7. **Replace `EnsureCreatedAsync` if any remain** — already migrated, but worth re-grepping after every schema change.
8. **DLQ alerting** — `_error` queue depth > 0 should page; the alert rule is a gitops concern (PrometheusRule) but the metric source is here.
9. **Contract tests across services** — add a Pact-style consumer-driven contract test so a service that publishes a new event field can't break a downstream consumer silently.
10. **Disaster drill** — run a chaos test (`docker compose pause rabbitmq`, then resume) to confirm consumer retry + outbox replay actually heal the saga in practice. Today's tests cover failure *injection* but not infra outage.

## Out-of-scope reminders

These belong to **gitops** repo, not `app/`:

- Kubernetes manifests, Helm charts, Kustomize overlays.
- Istio Gateway / VirtualService / DestinationRule / PeerAuthentication / AuthorizationPolicy.
- ArgoCD `Application` / `ApplicationSet` (app-of-apps).
- kube-prometheus-stack, ServiceMonitors, PrometheusRules, Alertmanager routes.
- RabbitMQ Cluster Operator, sealed-secrets / External Secrets, NetworkPolicies, HPA, PDB.

---

**Bottom line:** every original goal is satisfied within `app/` scope and verified by automated tests. Items in §"Recommended enhancements" are improvements, not gaps in the brief.
