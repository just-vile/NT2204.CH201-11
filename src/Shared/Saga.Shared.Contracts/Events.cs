namespace Saga.Shared.Contracts;

public sealed record OrderPlaced(
    Guid OrderId,
    Guid CustomerId,
    IReadOnlyList<OrderItem> Items,
    decimal TotalAmount,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record OrderCancelled(
    Guid OrderId,
    string Reason,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record OrderCompleted(
    Guid OrderId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record PaymentSucceeded(
    Guid OrderId,
    Guid PaymentId,
    Guid ReservationId,
    decimal Amount,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record PaymentFailed(
    Guid OrderId,
    string Reason,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record PaymentRefunded(
    Guid OrderId,
    Guid PaymentId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record InventoryReserved(
    Guid OrderId,
    Guid ReservationId,
    Guid CustomerId,
    decimal Amount,
    IReadOnlyList<OrderItem> Items,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record InventoryUnavailable(
    Guid OrderId,
    string MissingSku,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record InventoryReleased(
    Guid OrderId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;

public sealed record ShipmentDispatched(
    Guid OrderId,
    Guid ShipmentId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt) : ICorrelatedEvent;
