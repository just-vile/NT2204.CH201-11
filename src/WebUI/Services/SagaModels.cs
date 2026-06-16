namespace Saga.WebUI.Services;

public sealed record PlaceOrderItem(string Sku, int Quantity, decimal UnitPrice);

public sealed record PlaceOrderRequest(Guid CustomerId, List<PlaceOrderItem> Items);

public sealed record PlaceOrderResponse(Guid Id, string Status, Guid CorrelationId);

public sealed record OrderDto(
    Guid Id,
    Guid CustomerId,
    decimal TotalAmount,
    string Status,
    string? CancellationReason,
    Guid CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    List<OrderLineDto>? Items);

public sealed record OrderLineDto(Guid Id, Guid OrderId, string Sku, int Quantity, decimal UnitPrice);

public sealed record PaymentDto(
    Guid Id,
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string Status,
    string? FailureReason,
    Guid CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RefundedAt);

public sealed record ReservationDto(
    Guid Id,
    Guid OrderId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReleasedAt,
    List<ReservationLineDto>? Lines);

public sealed record ReservationLineDto(Guid Id, Guid ReservationId, string Sku, int Quantity);

public sealed record ShipmentDto(
    Guid Id,
    Guid OrderId,
    Guid ReservationId,
    string TrackingNumber,
    string Status,
    Guid CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DispatchedAt);

public sealed record StockDto(string Sku, int Available);

/// <summary>
/// Aggregate view of one saga from the UI's perspective. Any field may be null until
/// the corresponding service has acted on the saga.
/// </summary>
public sealed record SagaSnapshot(
    OrderDto? Order,
    PaymentDto? Payment,
    ReservationDto? Reservation,
    ShipmentDto? Shipment)
{
    public bool IsTerminal => Order?.Status is "Completed" or "Cancelled";
}

public sealed record TrackedOrder(Guid Id, string Label, DateTimeOffset PlacedAt);
