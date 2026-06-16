using MassTransit;
using Microsoft.EntityFrameworkCore;
using Saga.Shared.Contracts;
using Saga.ShippingService.Domain;
using Saga.ShippingService.Infrastructure;

namespace Saga.ShippingService.Consumers;

/// <summary>
/// Dispatches a shipment once payment has been authorized (the last forward step in the
/// reserve-then-charge saga). The reservation id rides through PaymentSucceeded so we
/// can link the shipment back to the inventory hold.
/// </summary>
public class PaymentSucceededConsumer(ShippingDbContext db, ILogger<PaymentSucceededConsumer> log) : IConsumer<PaymentSucceeded>
{
    public async Task Consume(ConsumeContext<PaymentSucceeded> ctx)
    {
        var msg = ctx.Message;
        var ct = ctx.CancellationToken;

        if (await db.Shipments.AsNoTracking().AnyAsync(s => s.OrderId == msg.OrderId, ct))
        {
            log.LogInformation("Shipment for order {OrderId} already exists; skipping", msg.OrderId);
            return;
        }

        var shipment = new Shipment
        {
            OrderId = msg.OrderId,
            ReservationId = msg.ReservationId,
            TrackingNumber = $"TRK-{Guid.NewGuid():N}"[..16],
            Status = ShipmentStatus.Dispatched,
            CorrelationId = msg.CorrelationId,
            DispatchedAt = DateTimeOffset.UtcNow
        };
        db.Shipments.Add(shipment);

        await ctx.Publish(new ShipmentDispatched(msg.OrderId, shipment.Id, msg.CorrelationId, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Shipment {ShipmentId} DISPATCHED for order {OrderId} ({Tracking})",
            shipment.Id, shipment.OrderId, shipment.TrackingNumber);
    }
}
