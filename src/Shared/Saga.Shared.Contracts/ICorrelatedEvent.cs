namespace Saga.Shared.Contracts;

/// <summary>
/// Marker contract for every saga event. Carries the saga correlation id and the wall-clock
/// timestamp at which the event was raised. The presence of a <see cref="Guid"/> property
/// named <c>CorrelationId</c> is what MassTransit uses (by convention) to populate
/// <c>ConsumeContext.CorrelationId</c> on the wire, so this contracts library deliberately
/// has no MassTransit reference of its own.
/// </summary>
public interface ICorrelatedEvent
{
    Guid CorrelationId { get; }
    DateTimeOffset OccurredAt { get; }
}

public sealed record OrderItem(string Sku, int Quantity, decimal UnitPrice);
