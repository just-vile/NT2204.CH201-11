using MassTransit;
using Microsoft.EntityFrameworkCore;
using Saga.PaymentService.Domain;
using Saga.PaymentService.Infrastructure;
using Saga.Shared.Contracts;

namespace Saga.PaymentService.Consumers;

/// <summary>
/// Charges the customer once inventory has been provisionally reserved. Inventory is
/// reserved first so we never charge a card for an order we can't fulfill.
/// </summary>
public class InventoryReservedConsumer(PaymentDbContext db, ILogger<InventoryReservedConsumer> log) : IConsumer<InventoryReserved>
{
    public async Task Consume(ConsumeContext<InventoryReserved> ctx)
    {
        var msg = ctx.Message;
        var ct = ctx.CancellationToken;

        var existingPayment = await db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrderId == msg.OrderId, ct);
        if (existingPayment is not null)
        {
            if (existingPayment.Status == PaymentStatus.CancelledBeforeCharge)
            {
                // Tombstone planted by OrderCancelledConsumer: order is dead, fail
                // forward so InventoryService releases the just-created reservation.
                await ctx.Publish(new PaymentFailed(
                    msg.OrderId, "order_cancelled", msg.CorrelationId, DateTimeOffset.UtcNow), ct);
                log.LogWarning("Skipping charge for cancelled order {OrderId}", msg.OrderId);
                return;
            }
            log.LogInformation("Payment for order {OrderId} already processed; skipping", msg.OrderId);
            return;
        }

        // Demo failure injection for the saga's failure modes:
        //   FAIL_PAY* SKU      => declined
        //   amount > 100_000   => declined as fraud
        //   STALL_* SKU        => hang the handler so the OrderService watchdog can trip
        var rejected = msg.Items.Any(i => i.Sku.StartsWith("FAIL_PAY", StringComparison.OrdinalIgnoreCase));
        var amountTooHigh = msg.Amount > 100_000m;
        var stall = msg.Items.Any(i => i.Sku.StartsWith("STALL_", StringComparison.OrdinalIgnoreCase));

        if (stall)
        {
            log.LogWarning("Stalling payment for order {OrderId} (STALL_ SKU) to trigger saga timeout", msg.OrderId);
            await Task.Delay(TimeSpan.FromMinutes(5), ct);
        }

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = msg.OrderId,
            CustomerId = msg.CustomerId,
            Amount = msg.Amount,
            CorrelationId = msg.CorrelationId
        };

        if (rejected || amountTooHigh)
        {
            payment.Decline(rejected ? "card_declined" : "amount_over_limit");
            db.Payments.Add(payment);

            await ctx.Publish(new PaymentFailed(msg.OrderId, payment.FailureReason!, msg.CorrelationId, DateTimeOffset.UtcNow), ct);
            await db.SaveChangesAsync(ct);
            log.LogWarning("Payment FAILED for order {OrderId} ({Reason})", msg.OrderId, payment.FailureReason);
            return;
        }

        payment.Authorize();
        db.Payments.Add(payment);

        await ctx.Publish(new PaymentSucceeded(
            msg.OrderId,
            payment.Id,
            msg.ReservationId,
            payment.Amount,
            msg.CorrelationId,
            DateTimeOffset.UtcNow), ct);

        await db.SaveChangesAsync(ct);
        log.LogInformation("Payment {PaymentId} SUCCEEDED for order {OrderId} amount {Amount}", payment.Id, msg.OrderId, payment.Amount);
    }
}

/// <summary>
/// Compensation: refunds a successful payment when the saga aborts post-charge
/// (currently only the saga-timeout watchdog at stage PaymentSucceeded triggers this).
/// Idempotent if already refunded; no-op if the payment was never charged.
/// </summary>
public class InventoryUnavailableConsumer(PaymentDbContext db, ILogger<InventoryUnavailableConsumer> log) : IConsumer<InventoryUnavailable>
{
    public async Task Consume(ConsumeContext<InventoryUnavailable> ctx)
    {
        var ct = ctx.CancellationToken;
        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == ctx.Message.OrderId, ct);
        if (payment is null) return;
        if (!payment.TryRefund(DateTimeOffset.UtcNow)) return;

        await ctx.Publish(new PaymentRefunded(payment.OrderId, payment.Id, ctx.Message.CorrelationId, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Payment {PaymentId} REFUNDED for order {OrderId} (saga abort post-payment)", payment.Id, payment.OrderId);
    }
}

/// <summary>
/// Reacts to an order being cancelled (typically by the saga-timeout watchdog) so
/// late-arriving InventoryReserved messages don't charge a card for a dead order.
/// If a payment has already been charged we refund it; otherwise we plant a
/// tombstone row so InventoryReservedConsumer can short-circuit.
/// </summary>
public class OrderCancelledConsumer(PaymentDbContext db, ILogger<OrderCancelledConsumer> log) : IConsumer<OrderCancelled>
{
    public async Task Consume(ConsumeContext<OrderCancelled> ctx)
    {
        var msg = ctx.Message;
        var ct = ctx.CancellationToken;

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == msg.OrderId, ct);

        if (payment is null)
        {
            // Tombstone: amount=0 placeholder so the unique OrderId index reserves the slot.
            db.Payments.Add(new Payment
            {
                Id = Guid.NewGuid(),
                OrderId = msg.OrderId,
                CustomerId = Guid.Empty,
                Amount = 0m,
                Status = PaymentStatus.CancelledBeforeCharge,
                CorrelationId = msg.CorrelationId
            });
            await db.SaveChangesAsync(ct);
            log.LogWarning("Tombstone payment planted for cancelled order {OrderId}", msg.OrderId);
            return;
        }

        if (!payment.TryRefund(DateTimeOffset.UtcNow)) return;

        await ctx.Publish(new PaymentRefunded(payment.OrderId, payment.Id, msg.CorrelationId, DateTimeOffset.UtcNow), ct);
        await db.SaveChangesAsync(ct);
        log.LogWarning("Payment {PaymentId} REFUNDED for order {OrderId} (OrderCancelled)", payment.Id, payment.OrderId);
    }
}
