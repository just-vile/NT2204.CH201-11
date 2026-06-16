using MassTransit;
using Microsoft.EntityFrameworkCore;
using Saga.OrderService.Domain;
using Saga.OrderService.Infrastructure;
using Saga.Shared.Contracts;
using Saga.Shared.Infrastructure;

namespace Saga.OrderService.Consumers;

public class PaymentFailedConsumer(OrderDbContext db, ILogger<PaymentFailedConsumer> log) : IConsumer<PaymentFailed>
{
    public async Task Consume(ConsumeContext<PaymentFailed> ctx)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == ctx.Message.OrderId, ctx.CancellationToken);
        if (order is null) return;

        if (!order.Cancel($"payment_failed:{ctx.Message.Reason}", DateTimeOffset.UtcNow))
            return;

        await ctx.Publish(
            new OrderCancelled(order.Id, order.CancellationReason!, ctx.Message.CorrelationId, DateTimeOffset.UtcNow),
            ctx.CancellationToken);
        await db.SaveChangesAsync(ctx.CancellationToken);
        SagaMetrics.RecordTerminal("cancelled", order.CancellationReason!);
        log.LogWarning("Order {OrderId} cancelled due to PaymentFailed", order.Id);
    }
}

public class InventoryUnavailableConsumer(OrderDbContext db, ILogger<InventoryUnavailableConsumer> log) : IConsumer<InventoryUnavailable>
{
    public async Task Consume(ConsumeContext<InventoryUnavailable> ctx)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == ctx.Message.OrderId, ctx.CancellationToken);
        if (order is null) return;

        if (!order.Cancel($"inventory_unavailable:{ctx.Message.MissingSku}", DateTimeOffset.UtcNow))
            return;

        await ctx.Publish(
            new OrderCancelled(order.Id, order.CancellationReason!, ctx.Message.CorrelationId, DateTimeOffset.UtcNow),
            ctx.CancellationToken);
        await db.SaveChangesAsync(ctx.CancellationToken);
        SagaMetrics.RecordTerminal("cancelled", order.CancellationReason!);
        log.LogWarning("Order {OrderId} cancelled due to InventoryUnavailable ({Sku})", order.Id, ctx.Message.MissingSku);
    }
}

public class ShipmentDispatchedConsumer(OrderDbContext db, ILogger<ShipmentDispatchedConsumer> log) : IConsumer<ShipmentDispatched>
{
    public async Task Consume(ConsumeContext<ShipmentDispatched> ctx)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == ctx.Message.OrderId, ctx.CancellationToken);
        if (order is null) return;

        if (!order.Complete(DateTimeOffset.UtcNow)) return;

        await ctx.Publish(
            new OrderCompleted(order.Id, ctx.Message.CorrelationId, DateTimeOffset.UtcNow),
            ctx.CancellationToken);
        await db.SaveChangesAsync(ctx.CancellationToken);
        SagaMetrics.RecordTerminal("completed", "shipped");
        log.LogInformation("Order {OrderId} completed (shipment {ShipmentId})", order.Id, ctx.Message.ShipmentId);
    }
}

// Stage-tracking only (no compensation, no publish): lets OrderTimeoutWatchdog decide
// which synthetic failure to emit if an order stalls past its SLA.
public class PaymentSucceededStageTracker(OrderDbContext db) : IConsumer<PaymentSucceeded>
{
    public async Task Consume(ConsumeContext<PaymentSucceeded> ctx)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == ctx.Message.OrderId, ctx.CancellationToken);
        if (order is null) return;
        if (!order.TryAdvanceStage(SagaStage.PaymentSucceeded, DateTimeOffset.UtcNow)) return;
        await db.SaveChangesAsync(ctx.CancellationToken);
    }
}

public class InventoryReservedStageTracker(OrderDbContext db) : IConsumer<InventoryReserved>
{
    public async Task Consume(ConsumeContext<InventoryReserved> ctx)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == ctx.Message.OrderId, ctx.CancellationToken);
        if (order is null) return;
        if (!order.TryAdvanceStage(SagaStage.InventoryReserved, DateTimeOffset.UtcNow)) return;
        await db.SaveChangesAsync(ctx.CancellationToken);
    }
}
