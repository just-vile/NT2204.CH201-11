using FluentAssertions;
using Saga.Shared.Contracts;
using Xunit;

namespace Saga.Shared.Contracts.Tests;

public class EventContractTests
{
    public static IEnumerable<object[]> AllEventTypes() => new[]
    {
        new object[] { typeof(OrderPlaced) },
        new object[] { typeof(OrderCancelled) },
        new object[] { typeof(OrderCompleted) },
        new object[] { typeof(PaymentSucceeded) },
        new object[] { typeof(PaymentFailed) },
        new object[] { typeof(PaymentRefunded) },
        new object[] { typeof(InventoryReserved) },
        new object[] { typeof(InventoryUnavailable) },
        new object[] { typeof(InventoryReleased) },
        new object[] { typeof(ShipmentDispatched) },
    };

    [Theory, MemberData(nameof(AllEventTypes))]
    public void Every_event_implements_ICorrelatedEvent(Type t)
        => typeof(ICorrelatedEvent).IsAssignableFrom(t).Should().BeTrue($"{t.Name} must carry CorrelationId/OccurredAt");

    [Theory, MemberData(nameof(AllEventTypes))]
    public void Every_event_exposes_a_Guid_CorrelationId_property_for_MassTransit_convention(Type t)
    {
        // MassTransit auto-populates ConsumeContext.CorrelationId from a property named
        // exactly "CorrelationId" of type Guid. We assert the convention here so a
        // breaking rename surfaces as a unit-test failure instead of a missing
        // correlation id at runtime.
        var prop = t.GetProperty("CorrelationId");
        prop.Should().NotBeNull($"{t.Name} must expose a CorrelationId property");
        prop!.PropertyType.Should().Be(typeof(Guid), $"{t.Name}.CorrelationId must be a Guid");
    }

    [Fact]
    public void OrderPlaced_carries_items_and_total()
    {
        var e = new OrderPlaced(Guid.NewGuid(), Guid.NewGuid(),
            new[] { new OrderItem("SKU-1", 2, 10m) },
            20m, Guid.NewGuid(), DateTimeOffset.UtcNow);
        e.Items.Should().HaveCount(1);
        e.TotalAmount.Should().Be(20m);
    }

    [Fact]
    public void InventoryReserved_carries_payment_inputs_for_event_carried_state_transfer()
    {
        // After the reserve-then-charge refactor, InventoryReserved is the input to PaymentService.
        // It must carry CustomerId + Amount + Items so payment-service does not need to call
        // back into order-service.
        var items = new[] { new OrderItem("SKU-1", 2, 10m) };
        var e = new InventoryReserved(
            OrderId: Guid.NewGuid(),
            ReservationId: Guid.NewGuid(),
            CustomerId: Guid.NewGuid(),
            Amount: 20m,
            Items: items,
            CorrelationId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow);
        e.CustomerId.Should().NotBe(Guid.Empty);
        e.Amount.Should().Be(20m);
        e.Items.Should().BeEquivalentTo(items);
    }

    [Fact]
    public void PaymentSucceeded_carries_reservation_id_for_shipping()
    {
        // After the reserve-then-charge refactor, PaymentSucceeded is the input to ShippingService.
        // It must carry the ReservationId so the shipment can be linked to the inventory hold.
        var e = new PaymentSucceeded(
            OrderId: Guid.NewGuid(),
            PaymentId: Guid.NewGuid(),
            ReservationId: Guid.NewGuid(),
            Amount: 20m,
            CorrelationId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow);
        e.ReservationId.Should().NotBe(Guid.Empty);
        e.Amount.Should().Be(20m);
    }
}
