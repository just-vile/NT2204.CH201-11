using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Saga.OrderService.Domain;
using Saga.OrderService.Infrastructure;
using Saga.Shared.Contracts;
using Saga.Shared.Infrastructure;

namespace Saga.OrderService.Saga;

public sealed class OrderTimeoutWatchdog(
    IServiceScopeFactory scopeFactory,
    IOptions<SagaTimeoutOptions> options,
    ILogger<OrderTimeoutWatchdog> log) : BackgroundService
{
    private static readonly ActivitySource Activity = new(TelemetryConstants.SagaActivitySourceName);

    private readonly TimeSpan _scanInterval = options.Value.ScanInterval;
    private readonly TimeSpan _total = options.Value.Total;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation(
            "OrderTimeoutWatchdog started; scan interval {ScanInterval}, total SLA {Total}",
            _scanInterval, _total);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "OrderTimeoutWatchdog tick failed");
            }

            try
            {
                await Task.Delay(_scanInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();

        var cutoff = DateTimeOffset.UtcNow - _total;
        var stalled = await db.Orders
            .Where(o => o.Status == OrderStatus.Pending
                        && o.CreatedAt < cutoff
                        && o.CancellationReason == null)
            .ToListAsync(ct);

        if (stalled.Count == 0)
        {
            log.LogDebug("OrderTimeoutWatchdog tick: no stalled orders (cutoff {Cutoff:o})", cutoff);
            return;
        }

        var emitted = 0;
        foreach (var order in stalled)
        {
            await EmitTimeoutAsync(order, publish, ct);
            if (order.MarkTimeoutEmitted(DateTimeOffset.UtcNow))
            {
                emitted++;
            }
        }

        await db.SaveChangesAsync(ct);
        log.LogDebug(
            "OrderTimeoutWatchdog tick: emitted {Count} synthetic timeouts (cutoff {Cutoff:o})",
            emitted, cutoff);
    }

    private async Task EmitTimeoutAsync(Order order, IPublishEndpoint publish, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        using var activity = Activity.StartActivity("saga.timeout.emit", ActivityKind.Producer);
        activity?.SetTag("order.id", order.Id);
        activity?.SetTag("correlation.id", order.CorrelationId);
        activity?.SetTag("saga.stage", order.Stage.ToString());

        switch (order.Stage)
        {
            case SagaStage.OrderPlaced:
            {
                // Pre-reservation timeout: nothing to refund, nothing to release.
                // Synthetic InventoryUnavailable cancels the order via OrderService.InventoryUnavailableConsumer.
                var messageId = DeterministicGuid($"{order.Id}|timeout-pre-reserve");
                await publish.Publish(
                    new InventoryUnavailable(order.Id, "(timeout)", order.CorrelationId, now),
                    ctx => ctx.MessageId = messageId,
                    ct);
                activity?.SetTag("saga.timeout.event", nameof(InventoryUnavailable));
                log.LogInformation(
                    "Saga timeout: emitted synthetic InventoryUnavailable (pre-reserve) for order {OrderId} (stage {Stage}, messageId {MessageId})",
                    order.Id, order.Stage, messageId);
                break;
            }
            case SagaStage.InventoryReserved:
            {
                // Reservation exists, no payment yet. Synthetic PaymentFailed releases the
                // reservation (InventoryService.PaymentFailedConsumer) and cancels the order
                // (OrderService.PaymentFailedConsumer). No payment to refund.
                var messageId = DeterministicGuid($"{order.Id}|timeout-payment");
                await publish.Publish(
                    new PaymentFailed(order.Id, "saga_timeout", order.CorrelationId, now),
                    ctx => ctx.MessageId = messageId,
                    ct);
                activity?.SetTag("saga.timeout.event", nameof(PaymentFailed));
                log.LogInformation(
                    "Saga timeout: emitted synthetic PaymentFailed for order {OrderId} (stage {Stage}, messageId {MessageId})",
                    order.Id, order.Stage, messageId);
                break;
            }
            case SagaStage.PaymentSucceeded:
            {
                // Reserved + charged, but shipping never happened. Synthetic InventoryUnavailable
                // chains all three compensations:
                //   PaymentService.InventoryUnavailableConsumer refunds → publishes PaymentRefunded
                //   InventoryService.PaymentRefundedConsumer releases stock → publishes InventoryReleased
                //   OrderService.InventoryUnavailableConsumer cancels the order
                var messageId = DeterministicGuid($"{order.Id}|timeout-shipping");
                await publish.Publish(
                    new InventoryUnavailable(order.Id, "(timeout)", order.CorrelationId, now),
                    ctx => ctx.MessageId = messageId,
                    ct);
                activity?.SetTag("saga.timeout.event", nameof(InventoryUnavailable));
                log.LogInformation(
                    "Saga timeout: emitted synthetic InventoryUnavailable (post-payment) for order {OrderId} (stage {Stage}, messageId {MessageId})",
                    order.Id, order.Stage, messageId);
                break;
            }
            default:
                activity?.SetTag("saga.timeout.event", "none");
                log.LogDebug(
                    "Saga timeout: no synthetic event for order {OrderId} at stage {Stage}",
                    order.Id, order.Stage);
                break;
        }
    }

    private static Guid DeterministicGuid(string seed)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), hash);
        return new Guid(hash[..16]);
    }
}
