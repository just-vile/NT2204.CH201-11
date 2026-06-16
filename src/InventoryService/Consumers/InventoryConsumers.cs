using MassTransit;
using Microsoft.EntityFrameworkCore;
using Saga.InventoryService.Domain;
using Saga.InventoryService.Infrastructure;
using Saga.Shared.Contracts;

namespace Saga.InventoryService.Consumers;

/// <summary>
/// Reserves stock as soon as an order is placed (before payment is attempted).
/// This is the "reserve first, charge after" production-realistic ordering: we never
/// charge a customer's card and then have to refund because we couldn't fulfill.
/// </summary>
public class OrderPlacedConsumer(InventoryDbContext db, ILogger<OrderPlacedConsumer> log) : IConsumer<OrderPlaced>
{
    public async Task Consume(ConsumeContext<OrderPlaced> ctx)
    {
        var msg = ctx.Message;
        var ct = ctx.CancellationToken;

        var existing = await db.Reservations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.OrderId == msg.OrderId, ct);
        if (existing is not null)
        {
            if (existing.Status == ReservationStatus.CancelledBeforeReserved)
            {
                // Tombstone planted by OrderCancelledConsumer before OrderPlaced arrived.
                // Skip the reservation and signal the rest of the saga to compensate.
                await ctx.Publish(new InventoryUnavailable(
                    msg.OrderId, "(cancelled_before_reserved)", msg.CorrelationId, DateTimeOffset.UtcNow), ct);
                log.LogWarning("Skipping reservation for cancelled order {OrderId}", msg.OrderId);
                return;
            }
            log.LogInformation("Reservation for order {OrderId} already exists; skipping", msg.OrderId);
            return;
        }

        if (msg.Items is null || msg.Items.Count == 0)
        {
            // Bad contract instance — fail fast to the dead-letter queue.
            throw new InvalidOperationException($"OrderPlaced for {msg.OrderId} carries no items.");
        }

        var reservation = new Reservation { OrderId = msg.OrderId };
        foreach (var line in msg.Items)
        {
            // OUT_OF_STOCK_* SKUs are intentionally seeded with Available=0 (or absent),
            // so the same code path handles both the demo sentinel and real shortages.
            var stock = await db.Stock.FindAsync(new object?[] { line.Sku }, ct);
            if (stock is null || stock.Available < line.Quantity)
            {
                await ctx.Publish(new InventoryUnavailable(msg.OrderId, line.Sku, msg.CorrelationId, DateTimeOffset.UtcNow), ct);
                log.LogWarning("Inventory UNAVAILABLE for order {OrderId} sku {Sku} (stock={Stock} need={Need})",
                    msg.OrderId, line.Sku, stock?.Available ?? 0, line.Quantity);
                return;
            }
            // [ConcurrencyCheck] on Available means a concurrent decrement will throw
            // DbUpdateConcurrencyException on SaveChanges, which MassTransit retries.
            stock.Available -= line.Quantity;
            reservation.Lines.Add(new ReservationLine { Sku = line.Sku, Quantity = line.Quantity });
        }
        db.Reservations.Add(reservation);

        // PaymentService needs CustomerId + Amount to charge — event-carried state transfer
        // so it doesn't need to call back into order-service. Items are echoed through
        // for demo failure-injection sentinels (FAIL_PAY*, STALL_*) and downstream
        // observability; a real payment gateway would only need CustomerId + Amount.
        await ctx.Publish(new InventoryReserved(
            msg.OrderId,
            reservation.Id,
            msg.CustomerId,
            msg.TotalAmount,
            msg.Items,
            msg.CorrelationId,
            DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Inventory RESERVED for order {OrderId} (reservation {ReservationId})", msg.OrderId, reservation.Id);
    }
}

/// <summary>
/// Compensation 1: payment failed after reservation. Release stock so it's available
/// for other orders.
/// </summary>
public class PaymentFailedConsumer(InventoryDbContext db, ILogger<PaymentFailedConsumer> log) : IConsumer<PaymentFailed>
{
    public async Task Consume(ConsumeContext<PaymentFailed> ctx)
    {
        var ct = ctx.CancellationToken;
        var reservation = await db.Reservations
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.OrderId == ctx.Message.OrderId, ct);
        if (reservation is null) return;
        if (!reservation.TryRelease(DateTimeOffset.UtcNow)) return;

        foreach (var line in reservation.Lines)
        {
            var stock = await db.Stock.FindAsync(new object?[] { line.Sku }, ct);
            if (stock is not null) stock.Available += line.Quantity;
        }

        await ctx.Publish(new InventoryReleased(reservation.OrderId, ctx.Message.CorrelationId, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Inventory RELEASED for order {OrderId} (PaymentFailed)", reservation.OrderId);
    }
}

/// <summary>
/// Compensation 2: payment was charged then refunded (post-payment saga abort, e.g.
/// shipping timeout). The refund event signals "release whatever stock is still held".
/// Idempotent if the reservation has already been released by an earlier compensation.
/// </summary>
public class PaymentRefundedConsumer(InventoryDbContext db, ILogger<PaymentRefundedConsumer> log) : IConsumer<PaymentRefunded>
{
    public async Task Consume(ConsumeContext<PaymentRefunded> ctx)
    {
        var ct = ctx.CancellationToken;
        var reservation = await db.Reservations
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.OrderId == ctx.Message.OrderId, ct);
        if (reservation is null) return;
        if (!reservation.TryRelease(DateTimeOffset.UtcNow)) return;

        foreach (var line in reservation.Lines)
        {
            var stock = await db.Stock.FindAsync(new object?[] { line.Sku }, ct);
            if (stock is not null) stock.Available += line.Quantity;
        }

        await ctx.Publish(new InventoryReleased(reservation.OrderId, ctx.Message.CorrelationId, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Inventory RELEASED for order {OrderId} (PaymentRefunded)", reservation.OrderId);
    }
}

/// <summary>
/// Reacts to an order being cancelled (typically by the saga-timeout watchdog) so
/// late-arriving OrderPlaced messages don't reserve stock for an order that's already
/// dead. If the reservation hasn't happened yet we plant a tombstone row; if it has,
/// we release it like any other compensation.
/// </summary>
public class OrderCancelledConsumer(InventoryDbContext db, ILogger<OrderCancelledConsumer> log) : IConsumer<OrderCancelled>
{
    public async Task Consume(ConsumeContext<OrderCancelled> ctx)
    {
        var msg = ctx.Message;
        var ct = ctx.CancellationToken;

        var reservation = await db.Reservations
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.OrderId == msg.OrderId, ct);

        if (reservation is null)
        {
            // Tombstone: blocks any OrderPlaced that drains from the queue after this.
            db.Reservations.Add(new Reservation
            {
                OrderId = msg.OrderId,
                Status = ReservationStatus.CancelledBeforeReserved
            });
            await db.SaveChangesAsync(ct);
            log.LogWarning("Tombstone reservation planted for cancelled order {OrderId}", msg.OrderId);
            return;
        }

        if (!reservation.TryRelease(DateTimeOffset.UtcNow)) return;

        foreach (var line in reservation.Lines)
        {
            var stock = await db.Stock.FindAsync(new object?[] { line.Sku }, ct);
            if (stock is not null) stock.Available += line.Quantity;
        }

        await ctx.Publish(new InventoryReleased(reservation.OrderId, msg.CorrelationId, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Inventory RELEASED for order {OrderId} (OrderCancelled)", reservation.OrderId);
    }
}
