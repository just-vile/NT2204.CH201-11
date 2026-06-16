namespace Saga.ShippingService.Domain;

public enum ShipmentStatus { Pending = 0, Dispatched = 1 }

public class Shipment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid ReservationId { get; set; }
    public string TrackingNumber { get; set; } = default!;
    public ShipmentStatus Status { get; set; } = ShipmentStatus.Pending;
    public Guid CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DispatchedAt { get; set; }
}
