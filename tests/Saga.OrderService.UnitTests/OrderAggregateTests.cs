using FluentAssertions;
using Saga.OrderService.Domain;
using Xunit;

namespace Saga.OrderService.UnitTests;

/// <summary>
/// Unit tests for the Order aggregate state machine. These run in-process with no
/// dependencies and pin the saga invariants we rely on across the choreography:
///
///   - Stage may only advance forward.
///   - Once Cancelled, an Order cannot Complete; once Completed, it cannot Cancel.
///   - Compensations are idempotent (returning false for repeat events).
///   - The watchdog's MarkTimeoutEmitted is a one-shot guard: re-emission is a no-op.
/// </summary>
public class OrderAggregateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-15T10:00:00Z");

    private static Order NewOrder() => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        TotalAmount = 100m,
        CorrelationId = Guid.NewGuid()
    };

    [Fact]
    public void New_order_starts_pending_at_OrderPlaced()
    {
        var order = NewOrder();
        order.Status.Should().Be(OrderStatus.Pending);
        order.Stage.Should().Be(SagaStage.OrderPlaced);
    }

    [Fact]
    public void TryAdvanceStage_advances_forward_only()
    {
        var order = NewOrder();
        order.TryAdvanceStage(SagaStage.InventoryReserved, Now).Should().BeTrue();
        order.Stage.Should().Be(SagaStage.InventoryReserved);

        order.TryAdvanceStage(SagaStage.OrderPlaced, Now).Should().BeFalse("backwards transition is a no-op");
        order.Stage.Should().Be(SagaStage.InventoryReserved);

        order.TryAdvanceStage(SagaStage.InventoryReserved, Now).Should().BeFalse("same-stage transition is a no-op");

        order.TryAdvanceStage(SagaStage.PaymentSucceeded, Now).Should().BeTrue();
        order.Stage.Should().Be(SagaStage.PaymentSucceeded);
    }

    [Fact]
    public void TryAdvanceStage_rejects_terminal_stages()
    {
        var order = NewOrder();
        var act1 = () => order.TryAdvanceStage(SagaStage.Completed, Now);
        var act2 = () => order.TryAdvanceStage(SagaStage.Cancelled, Now);
        act1.Should().Throw<InvalidOperationException>();
        act2.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_is_idempotent()
    {
        var order = NewOrder();
        order.Cancel("payment_failed:card_declined", Now).Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        order.Stage.Should().Be(SagaStage.Cancelled);
        order.CancellationReason.Should().Be("payment_failed:card_declined");

        order.Cancel("another_reason", Now).Should().BeFalse("repeat cancel is a no-op");
        order.CancellationReason.Should().Be("payment_failed:card_declined");
    }

    [Fact]
    public void Cancel_after_Complete_throws()
    {
        var order = NewOrder();
        order.TryAdvanceStage(SagaStage.InventoryReserved, Now);
        order.TryAdvanceStage(SagaStage.PaymentSucceeded, Now);
        order.TryAdvanceStage(SagaStage.ShipmentDispatched, Now);
        order.Complete(Now);

        var act = () => order.Cancel("too_late", Now);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Complete_after_Cancel_throws()
    {
        var order = NewOrder();
        order.Cancel("payment_failed", Now);

        var act = () => order.Complete(Now);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Complete_is_idempotent()
    {
        var order = NewOrder();
        order.Complete(Now).Should().BeTrue();
        order.Complete(Now).Should().BeFalse();
    }

    [Fact]
    public void Cancel_requires_reason()
    {
        var order = NewOrder();
        var act = () => order.Cancel("   ", Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkTimeoutEmitted_is_one_shot()
    {
        var order = NewOrder();
        order.MarkTimeoutEmitted(Now).Should().BeTrue();
        order.CancellationReason.Should().Be("timeout_emitted");

        order.MarkTimeoutEmitted(Now).Should().BeFalse("the watchdog must not re-pick the same order on the next tick");
    }

    [Fact]
    public void MarkTimeoutEmitted_no_op_when_already_terminal()
    {
        var completed = NewOrder();
        completed.Complete(Now);
        completed.MarkTimeoutEmitted(Now).Should().BeFalse();

        var cancelled = NewOrder();
        cancelled.Cancel("payment_failed", Now);
        cancelled.MarkTimeoutEmitted(Now).Should().BeFalse();
    }

    [Fact]
    public void Happy_path_full_traversal()
    {
        var order = NewOrder();
        order.TryAdvanceStage(SagaStage.InventoryReserved, Now).Should().BeTrue();
        order.TryAdvanceStage(SagaStage.PaymentSucceeded, Now).Should().BeTrue();
        order.TryAdvanceStage(SagaStage.ShipmentDispatched, Now).Should().BeTrue();
        order.Complete(Now).Should().BeTrue();

        order.Status.Should().Be(OrderStatus.Completed);
        order.Stage.Should().Be(SagaStage.Completed);
        order.CancellationReason.Should().BeNull();
    }
}
