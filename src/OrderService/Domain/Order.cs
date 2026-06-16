using System.ComponentModel.DataAnnotations;

namespace Saga.OrderService.Domain;

public enum OrderStatus
{
    Pending = 0,
    Completed = 1,
    Cancelled = 2
}

public enum SagaStage
{
    OrderPlaced = 0,
    InventoryReserved = 1,
    PaymentSucceeded = 2,
    ShipmentDispatched = 3,
    Completed = 4,
    Cancelled = 5
}

/// <summary>
/// Order aggregate root. Owns the saga state machine: every legal transition is a method
/// on this type and every illegal transition either no-ops (idempotent) or throws.
///
/// `[ConcurrencyCheck]` on <see cref="Status"/> and <see cref="Stage"/> makes EF Core
/// include them in UPDATE WHERE clauses, so concurrent writes from saga consumers
/// surface as <c>DbUpdateConcurrencyException</c> instead of silently losing updates.
/// MassTransit's in-process retry policy reconciles by reloading and replaying.
/// </summary>
public class Order
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }

    [ConcurrencyCheck]
    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    [ConcurrencyCheck]
    public SagaStage Stage { get; set; } = SagaStage.OrderPlaced;

    public string? CancellationReason { get; set; }
    public Guid CorrelationId { get; set; }
    public string IdempotencyKey { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<OrderLine> Items { get; set; } = new();

    /// <summary>
    /// Advances the saga stage forward only, while the order is still pending.
    /// Idempotent for duplicate or out-of-order events. Returns true if the aggregate
    /// changed (and therefore should be persisted).
    /// </summary>
    public bool TryAdvanceStage(SagaStage next, DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending) return false;
        if (next <= Stage) return false;
        if (next is SagaStage.Completed or SagaStage.Cancelled)
            throw new InvalidOperationException(
                $"Use {nameof(Complete)}/{nameof(Cancel)} instead of TryAdvanceStage for terminal stages.");

        Stage = next;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Cancels the order with a reason. Idempotent if already cancelled. Throws if the
    /// order is already completed.
    /// </summary>
    public bool Cancel(string reason, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));
        if (Status == OrderStatus.Completed)
            throw new InvalidOperationException($"Order {Id} is already completed and cannot be cancelled.");
        if (Status == OrderStatus.Cancelled) return false;

        Status = OrderStatus.Cancelled;
        Stage = SagaStage.Cancelled;
        CancellationReason = reason;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Marks the order completed. Idempotent if already completed. Throws if the order
    /// has been cancelled.
    /// </summary>
    public bool Complete(DateTimeOffset now)
    {
        if (Status == OrderStatus.Cancelled)
            throw new InvalidOperationException($"Order {Id} is cancelled and cannot be completed.");
        if (Status == OrderStatus.Completed) return false;

        Status = OrderStatus.Completed;
        Stage = SagaStage.Completed;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Records that the saga-timeout watchdog has emitted a synthetic failure event for
    /// this order. The cancellation-reason flag prevents the watchdog re-emitting on the
    /// next tick before the failure has been processed by the saga.
    /// </summary>
    public bool MarkTimeoutEmitted(DateTimeOffset now)
    {
        if (Status != OrderStatus.Pending) return false;
        if (CancellationReason is not null) return false;

        CancellationReason = "timeout_emitted";
        UpdatedAt = now;
        return true;
    }
}

public class OrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Sku { get; set; } = default!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}
