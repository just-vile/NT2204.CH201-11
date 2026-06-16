using System.ComponentModel.DataAnnotations;

namespace Saga.InventoryService.Domain;

/// <summary>
/// Per-SKU on-hand stock. <see cref="Available"/> carries <c>[ConcurrencyCheck]</c> so
/// concurrent reservations cannot over-sell: the second SaveChanges throws
/// <c>DbUpdateConcurrencyException</c>, which MassTransit's retry policy reconciles
/// by reloading the row.
/// </summary>
public class ProductStock
{
    public string Sku { get; set; } = default!;

    [ConcurrencyCheck]
    public int Available { get; set; }
}

public enum ReservationStatus { Reserved = 0, Released = 1, CancelledBeforeReserved = 2 }

/// <summary>
/// Reservation aggregate. <see cref="Status"/> carries <c>[ConcurrencyCheck]</c> so
/// out-of-order or duplicate compensation events cannot transition the row twice.
/// </summary>
public class Reservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }

    [ConcurrencyCheck]
    public ReservationStatus Status { get; set; } = ReservationStatus.Reserved;

    public List<ReservationLine> Lines { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Releases an active reservation. Idempotent if already released or tombstoned.</summary>
    public bool TryRelease(DateTimeOffset now)
    {
        if (Status != ReservationStatus.Reserved) return false;

        Status = ReservationStatus.Released;
        ReleasedAt = now;
        return true;
    }
}

public class ReservationLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReservationId { get; set; }
    public string Sku { get; set; } = default!;
    public int Quantity { get; set; }
}
