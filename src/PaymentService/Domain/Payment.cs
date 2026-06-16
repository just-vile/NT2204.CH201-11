using System.ComponentModel.DataAnnotations;

namespace Saga.PaymentService.Domain;

public enum PaymentStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3,
    CancelledBeforeCharge = 4
}

/// <summary>
/// Payment aggregate. Encapsulates valid status transitions; <see cref="Status"/> carries
/// <c>[ConcurrencyCheck]</c> so concurrent saga consumers cannot lose updates.
/// </summary>
public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }

    [ConcurrencyCheck]
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public string? FailureReason { get; set; }
    public Guid CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RefundedAt { get; set; }

    /// <summary>Authorizes a pending payment.</summary>
    public void Authorize()
    {
        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException($"Payment {Id} cannot be authorized: status is {Status}.");
        Status = PaymentStatus.Succeeded;
    }

    /// <summary>Declines a pending payment with a reason.</summary>
    public void Decline(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));
        if (Status != PaymentStatus.Pending)
            throw new InvalidOperationException($"Payment {Id} cannot be declined: status is {Status}.");
        Status = PaymentStatus.Failed;
        FailureReason = reason;
    }

    /// <summary>
    /// Refunds a successful payment. Idempotent if already refunded; no-op if the payment
    /// was never charged (e.g. a stale InventoryUnavailable arrives after a decline).
    /// </summary>
    public bool TryRefund(DateTimeOffset now)
    {
        if (Status == PaymentStatus.Refunded) return false;
        if (Status != PaymentStatus.Succeeded) return false;

        Status = PaymentStatus.Refunded;
        RefundedAt = now;
        return true;
    }
}
