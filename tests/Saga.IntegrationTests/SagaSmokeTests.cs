using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Saga.IntegrationTests.Infrastructure;
using Saga.Shared.Contracts;
using Saga.Shared.Infrastructure;
using Xunit;

namespace Saga.IntegrationTests;

[Collection("Saga")]
public class SagaSmokeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly SagaTestFixture _fx;

    public SagaSmokeTests(SagaTestFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task HappyPath_publishes_OrderCompleted()
    {
        var correlationId = Guid.NewGuid();
        var orderId = await PlaceOrderAsync(correlationId, new[]
        {
            new PlaceItem("SKU-001", 2, 30m),
            new PlaceItem("SKU-002", 1, 40m)
        });

        var completed = await _fx.Collector.WaitFor<OrderCompleted>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId,
            Budget);

        completed.Should().NotBeNull("the saga should reach OrderCompleted within the deadline");

        var order = await GetJsonAsync(_fx.OrderClient, $"/orders/{orderId}");
        order.GetProperty("status").GetString().Should().Be("Completed");

        var payment = await GetJsonAsync(_fx.PaymentClient, $"/payments/by-order/{orderId}");
        payment.GetProperty("status").GetString().Should().Be("Succeeded");

        var reservation = await GetJsonAsync(_fx.InventoryClient, $"/reservations/by-order/{orderId}");
        reservation.GetProperty("status").GetString().Should().Be("Reserved");

        var shipment = await GetJsonAsync(_fx.ShippingClient, $"/shipments/by-order/{orderId}");
        shipment.GetProperty("status").GetString().Should().Be("Dispatched");

        var terminal = await _fx.TerminalMetrics.WaitFor(
            m => m.Outcome == "completed" && m.Reason == "shipped",
            Budget);
        terminal.Should().NotBeNull("saga.terminal{outcome=completed,reason=shipped} should be emitted when the saga reaches OrderCompleted");
    }

    [Fact]
    public async Task PaymentFailed_results_in_OrderCancelled_and_InventoryReleased()
    {
        var correlationId = Guid.NewGuid();
        // amount > 100_000 triggers the PaymentService failure injection (amount_over_limit).
        // In the reserve-then-charge flow, the reservation already exists by the time payment
        // is attempted, so the compensation chain must release the stock as well.
        var orderId = await PlaceOrderAsync(correlationId, new[]
        {
            new PlaceItem("SKU-001", 1, 200_000m)
        });

        var failed = await _fx.Collector.WaitFor<PaymentFailed>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId,
            Budget);
        failed.Should().NotBeNull("PaymentService should publish PaymentFailed for the over-limit order");

        var released = await _fx.Collector.WaitFor<InventoryReleased>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId,
            Budget);
        released.Should().NotBeNull("InventoryService should publish InventoryReleased to compensate the reservation");

        var cancelled = await _fx.Collector.WaitFor<OrderCancelled>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId,
            Budget);
        cancelled.Should().NotBeNull("OrderService should publish OrderCancelled in response to PaymentFailed");

        var order = await GetJsonAsync(_fx.OrderClient, $"/orders/{orderId}");
        order.GetProperty("status").GetString().Should().Be("Cancelled");

        var reservation = await GetJsonAsync(_fx.InventoryClient, $"/reservations/by-order/{orderId}");
        reservation.GetProperty("status").GetString().Should().Be("Released",
            "the saga must release the provisional reservation when payment subsequently fails");

        (await _fx.ShippingClient.GetAsync($"/shipments/by-order/{orderId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "no shipment should be dispatched when payment fails");

        var terminal = await _fx.TerminalMetrics.WaitFor(
            m => m.Outcome == "cancelled" && m.Reason != null && m.Reason.StartsWith("payment_failed:", StringComparison.Ordinal),
            Budget);
        terminal.Should().NotBeNull("saga.terminal{outcome=cancelled,reason=payment_failed:*} should be emitted when payment fails");
    }

    [Fact]
    public async Task InventoryUnavailable_results_in_OrderCancelled_with_no_payment()
    {
        var correlationId = Guid.NewGuid();
        // SKU starting with OUT_OF_STOCK_ triggers the InventoryService failure injection.
        // In the reserve-then-charge flow, inventory is the first hop, so payment never
        // even runs — there is nothing to refund.
        var orderId = await PlaceOrderAsync(correlationId, new[]
        {
            new PlaceItem("OUT_OF_STOCK_BANANA", 1, 50m)
        });

        var inventoryUnavailable = await _fx.Collector.WaitFor<InventoryUnavailable>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId,
            Budget);
        inventoryUnavailable.Should().NotBeNull("InventoryService should publish InventoryUnavailable for the OUT_OF_STOCK_ sentinel");

        var cancelled = await _fx.Collector.WaitFor<OrderCancelled>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId,
            Budget);
        cancelled.Should().NotBeNull("OrderService should publish OrderCancelled in response to InventoryUnavailable");

        var order = await GetJsonAsync(_fx.OrderClient, $"/orders/{orderId}");
        order.GetProperty("status").GetString().Should().Be("Cancelled");

        // PaymentService.OrderCancelledConsumer plants a tombstone so a late InventoryReserved
        // can short-circuit without charging. Either no row or a CancelledBeforeCharge row is fine;
        // the contract is "no money taken".
        var payResp = await _fx.PaymentClient.GetAsync($"/payments/by-order/{orderId}");
        if (payResp.StatusCode == HttpStatusCode.OK)
        {
            var pay = await payResp.Content.ReadFromJsonAsync<JsonElement>();
            pay.GetProperty("status").GetString().Should().Be("CancelledBeforeCharge");
        }
        else
        {
            payResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        (await _fx.ShippingClient.GetAsync($"/shipments/by-order/{orderId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "no shipment should be dispatched when inventory is unavailable");
    }    [Fact]
    public async Task Timeout_triggers_compensation()
    {
        var correlationId = Guid.NewGuid();
        // STALL_ SKU makes PaymentService delay 5 minutes after the reservation is taken.
        // The OrderTimeoutWatchdog (overridden to 8s in the fixture) sees the order stuck
        // at stage InventoryReserved and emits a synthetic PaymentFailed, which both
        // releases the reservation and cancels the order.
        var orderId = await PlaceOrderAsync(correlationId, new[]
        {
            new PlaceItem("STALL_ME", 1, 25m)
        });

        var failed = await _fx.Collector.WaitFor<PaymentFailed>(
            e => e.CorrelationId == correlationId
                 && e.OrderId == orderId
                 && e.Reason == "saga_timeout",
            Budget);
        failed.Should().NotBeNull("the watchdog should emit a synthetic PaymentFailed with reason 'saga_timeout'");

        var released = await _fx.Collector.WaitFor<InventoryReleased>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId,
            Budget);
        released.Should().NotBeNull("InventoryService should release the reservation in response to the synthetic PaymentFailed");

        var cancelled = await _fx.Collector.WaitFor<OrderCancelled>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId,
            Budget);
        cancelled.Should().NotBeNull("OrderService should cancel the order after the synthetic PaymentFailed");

        var order = await GetJsonAsync(_fx.OrderClient, $"/orders/{orderId}");
        order.GetProperty("status").GetString().Should().Be("Cancelled");
        var reason = order.GetProperty("cancellationReason").GetString();
        reason.Should().BeOneOf("timeout_emitted", "payment_failed:saga_timeout");

        var reservation = await GetJsonAsync(_fx.InventoryClient, $"/reservations/by-order/{orderId}");
        reservation.GetProperty("status").GetString().Should().Be("Released");
    }

    [Fact]
    public async Task OrderCancelled_BeforeReservation_DoesNotChargeCustomer()
    {
        // Simulate the bug scenario: order was cancelled (e.g. by saga-timeout watchdog)
        // before InventoryService had a chance to consume OrderPlaced. A subsequent
        // OrderPlaced delivered from the queue must NOT reserve stock or charge the card.
        var orderId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        await _fx.PublishAsync(new OrderCancelled(
            orderId, "test_cancelled_before_reservation", correlationId, DateTimeOffset.UtcNow));

        // Tombstones land in both services.
        await Eventually(async () =>
        {
            var resp = await _fx.InventoryClient.GetAsync($"/reservations/by-order/{orderId}");
            if (resp.StatusCode != HttpStatusCode.OK) return false;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("status").GetString() == "CancelledBeforeReserved";
        }, Budget, "inventory tombstone should be planted");

        await Eventually(async () =>
        {
            var resp = await _fx.PaymentClient.GetAsync($"/payments/by-order/{orderId}");
            if (resp.StatusCode != HttpStatusCode.OK) return false;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("status").GetString() == "CancelledBeforeCharge";
        }, Budget, "payment tombstone should be planted");

        // Now the late OrderPlaced drains from the queue. InventoryService must short-circuit
        // and emit InventoryUnavailable instead of reserving stock.
        var customerId = Guid.NewGuid();
        var items = new List<OrderItem> { new("SKU-001", 1, 10m) };
        await _fx.PublishAsync(new OrderPlaced(
            orderId, customerId, items, 10m, correlationId, DateTimeOffset.UtcNow));

        var unavailable = await _fx.Collector.WaitFor<InventoryUnavailable>(
            e => e.OrderId == orderId
                 && e.CorrelationId == correlationId
                 && e.MissingSku == "(cancelled_before_reserved)",
            Budget);
        unavailable.Should().NotBeNull("the late OrderPlaced must be rejected via the tombstone branch");

        // Reservation row must still be the tombstone (not flipped to Reserved).
        var resJson = await GetJsonAsync(_fx.InventoryClient, $"/reservations/by-order/{orderId}");
        resJson.GetProperty("status").GetString().Should().Be("CancelledBeforeReserved");

        // Stock for SKU-001 must not have been decremented by the rejected reservation.
        var stockJson = await GetJsonAsync(_fx.InventoryClient, "/stock/SKU-001");
        stockJson.GetProperty("available").GetInt32().Should()
            .BeGreaterThanOrEqualTo(0, "tombstone path must not decrement stock");

        // No charge ever happened: payment row remains the tombstone.
        var payJson = await GetJsonAsync(_fx.PaymentClient, $"/payments/by-order/{orderId}");
        payJson.GetProperty("status").GetString().Should().Be("CancelledBeforeCharge");
        payJson.GetProperty("amount").GetDecimal().Should().Be(0m);

        // No PaymentSucceeded should appear for this orderId.
        var stray = _fx.Collector.All
            .Where(r => r.Type == typeof(PaymentSucceeded))
            .Select(r => (PaymentSucceeded)r.Payload)
            .Any(p => p.OrderId == orderId);
        stray.Should().BeFalse("no PaymentSucceeded must be observed for a cancelled order");
    }

    [Fact]
    public async Task OrderCancelled_AfterPayment_RefundsAndReleases()
    {
        // A real order has progressed through Reserved + PaymentSucceeded. An OrderCancelled
        // arriving afterwards (e.g. operator-issued, or a delayed compensation) must drive
        // both the refund and the stock release.
        var correlationId = Guid.NewGuid();
        var orderId = await PlaceOrderAsync(correlationId, new[]
        {
            new PlaceItem("SKU-001", 1, 50m)
        });

        var paid = await _fx.Collector.WaitFor<PaymentSucceeded>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId, Budget);
        paid.Should().NotBeNull("payment must have succeeded before we cancel");

        await _fx.PublishAsync(new OrderCancelled(
            orderId, "test_cancelled_after_payment", correlationId, DateTimeOffset.UtcNow));

        var refunded = await _fx.Collector.WaitFor<PaymentRefunded>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId, Budget);
        refunded.Should().NotBeNull("PaymentService must refund in response to OrderCancelled");

        var released = await _fx.Collector.WaitFor<InventoryReleased>(
            e => e.CorrelationId == correlationId && e.OrderId == orderId, Budget);
        released.Should().NotBeNull("InventoryService must release the reservation in response to OrderCancelled");

        await Eventually(async () =>
        {
            var resp = await _fx.PaymentClient.GetAsync($"/payments/by-order/{orderId}");
            if (resp.StatusCode != HttpStatusCode.OK) return false;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("status").GetString() == "Refunded";
        }, Budget, "payment row should converge to Refunded");

        await Eventually(async () =>
        {
            var resp = await _fx.InventoryClient.GetAsync($"/reservations/by-order/{orderId}");
            if (resp.StatusCode != HttpStatusCode.OK) return false;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("status").GetString() == "Released";
        }, Budget, "reservation should converge to Released");
    }

    private static async Task Eventually(Func<Task<bool>> probe, TimeSpan timeout, string because)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { if (await probe()) return; } catch { /* swallow transient */ }
            await Task.Delay(200);
        }
        throw new Xunit.Sdk.XunitException($"Eventually(...) timed out after {timeout}: {because}");
    }

    // ------------------------------------------------------------------

    private async Task<Guid> PlaceOrderAsync(Guid correlationId, IEnumerable<PlaceItem> items)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                customerId = Guid.NewGuid(),
                items = items.Select(i => new { sku = i.Sku, quantity = i.Quantity, unitPrice = i.UnitPrice })
            })
        };
        req.Headers.TryAddWithoutValidation(TelemetryConstants.CorrelationHeader, correlationId.ToString());
        req.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var resp = await _fx.OrderClient.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, $"POST /orders body: {await resp.Content.ReadAsStringAsync()}");

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private sealed record PlaceItem(string Sku, int Quantity, decimal UnitPrice);
}

