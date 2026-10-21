namespace Nexus.Infrastructure.Orders;

/// <summary>Knobs for order creation. Every one has a default that ships.</summary>
public sealed class OrderOptions
{
    /// <summary>
    /// A denial-of-service bound, not a product rule: each item becomes a PDF that
    /// Stage C watermarks and streams, and twenty is comfortably more than any real
    /// order while keeping a malicious cart from asking for a hundred-item stamping job.
    /// </summary>
    public int MaxCartItems { get; set; } = 20;

    public int MaxNameLength { get; set; } = 128;

    public int MinNameLength { get; set; } = 2;

    public int MaxEmailLength { get; set; } = 256;

    /// <summary>
    /// How long a Pending order stays payable. Mirrors <c>Tap:AbandonAfterHours</c>;
    /// the reconciliation sweep fails the order after this, and a late capture is still
    /// legal afterwards (B4).
    /// </summary>
    public int AbandonAfterHours { get; set; } = 24;

    /// <summary>
    /// How many times the whole unit of work is re-run after an order-number collision.
    /// Three is enough — a fourth collision in one day-prefix is not a thing that
    /// happens — and an unbounded retry under a real fault is worse than a failure.
    /// </summary>
    public int MaxCreateAttempts { get; set; } = 3;
}
