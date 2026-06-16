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
public class OrderIdempotencyTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly SagaTestFixture _fx;

    public OrderIdempotencyTests(SagaTestFixture fx)
    {
        _fx = fx;
    }

    [Fact]
    public async Task Idempotent_replay_returns_same_order_and_publishes_once()
    {
        var correlationId = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("N");
        var customerId = Guid.NewGuid();

        var first = await PostOrderAsync(correlationId, key, customerId, "SKU-001", 1, 30m);
        first.StatusCode.Should().Be(HttpStatusCode.Created,
            $"first POST should create the order. body: {await first.Content.ReadAsStringAsync()}");
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var orderId = firstBody.GetProperty("id").GetGuid();

        var second = await PostOrderAsync(correlationId, key, customerId, "SKU-001", 1, 30m);
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            $"replay with same key should return 200. body: {await second.Content.ReadAsStringAsync()}");
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        secondBody.GetProperty("id").GetGuid().Should().Be(orderId,
            "the replay must return the original orderId");
        secondBody.GetProperty("status").GetString().Should().Be("Pending",
            "the replay must return the original placement status, not the live saga status");
        secondBody.GetProperty("correlationId").GetGuid().Should().Be(correlationId);

        var placed = await _fx.Collector.WaitFor<OrderPlaced>(
            e => e.OrderId == orderId && e.CorrelationId == correlationId,
            Budget);
        placed.Should().NotBeNull("the original POST should have published exactly one OrderPlaced");

        // Grace period to make sure no second OrderPlaced sneaks in.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var placedCount = _fx.Collector.All
            .Where(r => r.Type == typeof(OrderPlaced))
            .Select(r => (OrderPlaced)r.Payload)
            .Count(e => e.OrderId == orderId);
        placedCount.Should().Be(1, "the replay must not produce a second OrderPlaced event");

        var completed = await _fx.Collector.WaitFor<OrderCompleted>(
            e => e.OrderId == orderId && e.CorrelationId == correlationId,
            Budget);
        completed.Should().NotBeNull("the de-duped order must still progress to OrderCompleted");

        var order = await GetJsonAsync(_fx.OrderClient, $"/orders/{orderId}");
        order.GetProperty("status").GetString().Should().Be("Completed");
    }

    [Fact]
    public async Task Missing_idempotency_key_returns_400()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                customerId = Guid.NewGuid(),
                items = new[] { new { sku = "SKU-001", quantity = 1, unitPrice = 10m } }
            })
        };

        var resp = await _fx.OrderClient.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"missing Idempotency-Key header must be a 400. body: {await resp.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Different_keys_create_distinct_orders()
    {
        var correlation1 = Guid.NewGuid();
        var correlation2 = Guid.NewGuid();
        var customerId = Guid.NewGuid();
        var key1 = Guid.NewGuid().ToString("N");
        var key2 = Guid.NewGuid().ToString("N");

        var r1 = await PostOrderAsync(correlation1, key1, customerId, "SKU-001", 1, 15m);
        r1.StatusCode.Should().Be(HttpStatusCode.Created);
        var id1 = (await r1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var r2 = await PostOrderAsync(correlation2, key2, customerId, "SKU-001", 1, 15m);
        r2.StatusCode.Should().Be(HttpStatusCode.Created);
        var id2 = (await r2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        id1.Should().NotBe(id2, "two different idempotency keys must yield two distinct orders");

        var placed1 = await _fx.Collector.WaitFor<OrderPlaced>(
            e => e.OrderId == id1 && e.CorrelationId == correlation1, Budget);
        placed1.Should().NotBeNull();

        var placed2 = await _fx.Collector.WaitFor<OrderPlaced>(
            e => e.OrderId == id2 && e.CorrelationId == correlation2, Budget);
        placed2.Should().NotBeNull();
    }

    private Task<HttpResponseMessage> PostOrderAsync(
        Guid correlationId,
        string idempotencyKey,
        Guid customerId,
        string sku,
        int quantity,
        decimal unitPrice)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(new
            {
                customerId,
                items = new[] { new { sku, quantity, unitPrice } }
            })
        };
        req.Headers.TryAddWithoutValidation(TelemetryConstants.CorrelationHeader, correlationId.ToString());
        req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return _fx.OrderClient.SendAsync(req);
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }
}
