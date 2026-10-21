namespace Nexus.Domain.Entities;

/// <summary>One attempt to pay an order. A buyer who retries gets a second row.</summary>
public class PaymentAttempt
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    /// <summary>1-based, unique within the order.</summary>
    public int AttemptNumber { get; set; }

    /// <summary>
    /// What we send as both <c>reference.transaction</c> and <c>reference.idempotent</c>.
    /// No hyphens: KNET's trackid is not documented to accept them, so we do not risk it.
    /// </summary>
    public required string IdempotentReference { get; set; }

    public PaymentAttemptStatus Status { get; set; } = PaymentAttemptStatus.Initiated;

    public string? TapChargeId { get; set; }
    public string? TapPaymentReference { get; set; }
    public string? TapGatewayReference { get; set; }
    public string? PaymentMethod { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }

    public decimal AmountCharged { get; set; }
    public string Currency { get; set; } = "KWD";

    /// <summary>Tap's hosted page, where the buyer was sent.</summary>
    public string? TransactionUrl { get; set; }

    /// <summary>Touched by the reconciliation job only.</summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>
    /// Touched by the return page only. Kept apart from <see cref="LastPolledAt"/> so a
    /// worried buyer refreshing the result page cannot starve their own order of
    /// reconciliation.
    /// </summary>
    public DateTimeOffset? LastReturnPolledAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One delivery of a Tap notification, verified or not.
/// <para>
/// The uniqueness index is <b>filtered on <see cref="HashValid"/></b>. The repository is
/// public, so the webhook URL and the body shape are known: without the filter anyone can
/// POST a forged body carrying a real charge id, occupy the idempotency slot, and the
/// genuine notification that follows is swallowed as a duplicate — a paid customer with no
/// book, caused by an anonymous request.
/// </para>
/// </summary>
public class WebhookEvent
{
    public long Id { get; set; }

    /// <summary>The body's own id: <c>chg_…</c> for a charge, <c>re_…</c> for a refund. This is what is hashed as <c>x_id</c>.</summary>
    public required string TapObjectId { get; set; }

    public TapObjectType ObjectType { get; set; }

    /// <summary>Tap's status string, as delivered: CAPTURED, AUTHORIZED, …</summary>
    public required string Status { get; set; }

    public int? OrderId { get; set; }
    public Order? Order { get; set; }

    public bool HashValid { get; set; }
    public WebhookOutcome Outcome { get; set; }
    public string? RejectionReason { get; set; }

    /// <summary>Full only when <see cref="HashValid"/>. A rejected delivery keeps the hash and the first 2 KB.</summary>
    public string? RawBody { get; set; }

    public string? RawBodySha256 { get; set; }

    public int Attempt { get; set; }
    public string? RemoteIp { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}

public class Refund
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public int? PaymentAttemptId { get; set; }
    public PaymentAttempt? PaymentAttempt { get; set; }

    public string? TapRefundId { get; set; }

    public decimal AmountKwd { get; set; }
    public RefundStatus Status { get; set; } = RefundStatus.Pending;
    public RefundOrigin Origin { get; set; }

    public string? Reason { get; set; }
    public string? RequestedByAdminUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>An append-only trail of what moved an order's state and why.</summary>
public class OrderEvent
{
    public long Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public OrderStatus? FromStatus { get; set; }
    public OrderStatus ToStatus { get; set; }
    public PaymentSource Source { get; set; }

    public long? WebhookEventId { get; set; }
    public WebhookEvent? WebhookEvent { get; set; }

    public int? PaymentAttemptId { get; set; }
    public PaymentAttempt? PaymentAttempt { get; set; }

    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>
/// A cooperative lease so two app-pool workers cannot run the same background job at
/// once. Two rows: <c>payment-reconciliation</c> and <c>delivery-maintenance</c>.
/// </summary>
public class JobLease
{
    public required string Name { get; set; }
    public required string HolderId { get; set; }
    public DateTimeOffset AcquiredAt { get; set; }
    public DateTimeOffset HeldUntil { get; set; }
}
