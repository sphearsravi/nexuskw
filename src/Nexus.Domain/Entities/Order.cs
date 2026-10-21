namespace Nexus.Domain.Entities;

/// <summary>
/// A purchase. Every money-bearing fact and every fact naming the human is
/// <b>snapshotted here</b>, never read live from <see cref="Customer"/> — that row
/// is mutable and, for a guest, writable from an unauthenticated request. The
/// watermark, the invoice and the email all read the snapshot.
/// </summary>
public class Order
{
    public int Id { get; set; }

    /// <summary>`KW-{yyMMdd}-{6 Crockford base32}`, 16 characters — inside Tap's reference limit.</summary>
    public required string OrderNumber { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public required string BuyerNameSnapshot { get; set; }
    public required string BuyerEmailSnapshot { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;

    /// <summary>Operator-facing, English. Never shown to a customer.</summary>
    public string? StatusDetail { get; set; }

    /// <summary>Set when a paid order needs a human: a withdrawn title, a surplus capture.</summary>
    public string? ReviewReason { get; set; }

    /// <summary>The checkout interface language, snapshotted. Drives the email and the invoice.</summary>
    public string Language { get; set; } = "en";

    public string Currency { get; set; } = "KWD";

    public decimal SubtotalKwd { get; set; }
    public decimal VatRate { get; set; }            // always 0 in v1 — Kuwait levies none
    public decimal VatAmount { get; set; }          // always 0 in v1
    public decimal TotalKwd { get; set; }

    /// <summary>Units of <see cref="Currency"/> per 1 KWD. Always 1 in v1.</summary>
    public decimal FxRate { get; set; } = 1m;

    /// <summary>The amount handed to Tap, and the amount re-checked against the webhook.</summary>
    public decimal TotalCharged { get; set; }

    /// <summary>Running total of confirmed refunds. Decides Refunded vs PartiallyRefunded.</summary>
    public decimal RefundedTotalKwd { get; set; }

    public string? TapChargeId { get; set; }
    public string? TapPaymentReference { get; set; }

    /// <summary>KNET's own reference — what shows on the buyer's bank statement.</summary>
    public string? TapGatewayReference { get; set; }

    public string? PaymentMethod { get; set; }
    public int PaymentAttemptCount { get; set; }
    public DateTimeOffset PaymentExpiresAt { get; set; }
    public string? LastPaymentFailureCode { get; set; }

    public string? IdempotencyKey { get; set; }

    /// <summary>Concurrency token for the payment state machine.</summary>
    public byte[]? RowVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    public ICollection<OrderItem> Items { get; set; } = [];

    /// <summary>Confirmed refunds so far plus <paramref name="amount"/>, for bounds checks.</summary>
    public decimal RefundCeiling() => TotalCharged - RefundedTotalKwd;

    /// <summary>
    /// Why <paramref name="target"/> is not a legal next state, or null when it is.
    /// <para>
    /// This adjudicates; it never assigns. <c>OrderPaymentStateMachine</c> runs the
    /// authorisation guards (signature, amount, currency) and the side effects, and
    /// calls here to decide whether the move itself is allowed.
    /// </para>
    /// <para>
    /// <b>Failed and Cancelled are not terminal.</b> Tap can capture after we have given
    /// up — reconciliation marked the order failed, then the money arrives. A system that
    /// refuses that transition takes a customer's money and never delivers the book, and
    /// nothing looks broken. The prior state is not the guard; the signature and the
    /// amount are.
    /// </para>
    /// </summary>
    public string? TransitionBlocker(OrderStatus target)
    {
        if (target == Status) return null;   // idempotent replay, not an error

        var legal = Status switch
        {
            OrderStatus.Pending => target is OrderStatus.Paid or OrderStatus.Failed or OrderStatus.Cancelled,
            OrderStatus.Failed => target is OrderStatus.Paid,                 // late capture
            OrderStatus.Cancelled => target is OrderStatus.Paid,              // late capture
            OrderStatus.Paid => target is OrderStatus.Refunded or OrderStatus.PartiallyRefunded,
            OrderStatus.PartiallyRefunded => target is OrderStatus.Refunded or OrderStatus.PartiallyRefunded,
            OrderStatus.Refunded => false,                                    // terminal
            _ => false
        };

        return legal ? null : $"{Status} cannot become {target}.";
    }

    public bool CanTransitionTo(OrderStatus target) => TransitionBlocker(target) is null;
}

public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public int BookId { get; set; }
    public Book? Book { get; set; }

    /// <summary>Captured at purchase, so a later price edit never rewrites history.</summary>
    public decimal UnitPriceKwd { get; set; }

    // Both languages: an Arabic invoice reprinted years from now needs its own text.
    public required string TitleEnSnapshot { get; set; }
    public required string TitleArSnapshot { get; set; }
    public required string AuthorEnSnapshot { get; set; }
    public required string AuthorArSnapshot { get; set; }

    // No Quantity: a book is a file, not a countable thing (ST-5).
}
