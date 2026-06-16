using System.Collections.Concurrent;
using MassTransit;

namespace Saga.IntegrationTests.Infrastructure;

/// <summary>
/// Thread-safe sink that records saga events observed off the shared RabbitMQ broker.
/// Each test filters by <see cref="CorrelationId"/> so the collector can be shared across tests.
/// </summary>
public sealed class EventCollector
{
    private readonly ConcurrentQueue<RecordedEvent> _events = new();

    public IReadOnlyCollection<RecordedEvent> All => _events.ToArray();

    public void Record<T>(ConsumeContext<T> ctx) where T : class
    {
        _events.Enqueue(new RecordedEvent(
            typeof(T),
            ctx.Message,
            ctx.MessageId,
            ctx.CorrelationId,
            DateTimeOffset.UtcNow));
    }

    public async Task<T?> WaitFor<T>(Predicate<T> match, TimeSpan timeout, CancellationToken ct = default)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var found = _events
                .Where(e => e.Type == typeof(T))
                .Select(e => (T)e.Payload)
                .FirstOrDefault(m => match(m));
            if (found is not null) return found;
            await Task.Delay(100, ct);
        }
        return null;
    }
}

public sealed record RecordedEvent(
    Type Type,
    object Payload,
    Guid? MessageId,
    Guid? CorrelationId,
    DateTimeOffset ObservedAt);
