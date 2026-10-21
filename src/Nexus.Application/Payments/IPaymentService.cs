using Nexus.Domain;
using Nexus.Domain.Entities;

namespace Nexus.Application.Payments;

/// <summary>The fields Tap is asked to charge. Amount is already formatted at the currency's scale.</summary>
public sealed record TapChargeRequest(
    decimal Amount,
    string Currency,
    string CustomerFirstName,
    string CustomerEmail,
    string Reference,
    string IdempotentReference,
    string RedirectUrl,
    string PostUrl,
    string Language,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>What Tap says about a charge, whether from a create, a poll or a webhook.</summary>
public sealed record TapChargeSnapshot(
    string Id,
    string Status,
    decimal Amount,
    string Currency,
    string? GatewayReference,
    string? PaymentReference,
    string? PaymentMethod,
    string? TransactionUrl,
    string? Created,
    string? FailureCode,
    string? FailureMessage);

/// <summary>Talks to Tap. The only thing in the system that holds the secret key.</summary>
public interface ITapClient
{
    Task<TapChargeSnapshot> CreateChargeAsync(TapChargeRequest request, CancellationToken ct = default);

    /// <summary>Used by the return page and by reconciliation. Never trusted to write state on its own.</summary>
    Task<TapChargeSnapshot?> GetChargeAsync(string chargeId, CancellationToken ct = default);

    Task<TapChargeSnapshot> CreateRefundAsync(
        string chargeId, decimal amount, string currency, string reason, CancellationToken ct = default);
}

/// <summary>
/// Verifies the <c>hashstring</c> header on a Tap notification.
/// <para>
/// The amount is hashed at the currency's own scale — KWD has three decimals. Send or
/// hash <c>6.5</c> where Tap hashed <c>6.500</c> and every signature fails, silently,
/// with nothing visibly wrong on the storefront and no order ever completing. The
/// implementation must be tested against Tap's own documented sample, not against
/// itself.
/// </para>
/// </summary>
public interface ITapSignature
{
    /// <summary>Builds the exact string Tap hashes, for testing and for diagnosis.</summary>
    string BuildToBeHashed(
        string id, decimal amount, Currency currency,
        string gatewayReference, string paymentReference, string status, string created);

    /// <summary>Fixed-time comparison against the header. False rejects the delivery.</summary>
    bool Verify(string toBeHashed, string providedHash);
}

public sealed record WebhookResult(WebhookOutcome Outcome, int? OrderId, string? Detail);

/// <summary>Handles an inbound Tap notification. The only thing that may mark an order paid.</summary>
public interface IPaymentService
{
    /// <summary>
    /// Verifies, deduplicates and applies a notification.
    /// <paramref name="rawBody"/> is the bytes as received — re-serialising changes them
    /// and breaks the hash.
    /// </summary>
    Task<WebhookResult> HandleWebhookAsync(
        string rawBody, string? hashHeader, string? remoteIp, CancellationToken ct = default);

    /// <summary>Starts a payment for a Pending order and returns Tap's hosted-page URL.</summary>
    Task<string> BeginPaymentAsync(int orderId, CancellationToken ct = default);

    /// <summary>Re-reads a charge from Tap and settles the order. Used by the return page and reconciliation.</summary>
    Task<WebhookResult> PollAndSettleAsync(int orderId, PaymentSource source, CancellationToken ct = default);
}

/// <summary>
/// Moves an order between states and runs the side effects inside the same transaction.
/// <para>
/// It orchestrates; <see cref="Order.TransitionBlocker"/> adjudicates. The state machine
/// runs the authorisation guards — signature, amount, currency — and calls the delivery
/// and email services; it never assigns <see cref="Order.Status"/> on its own authority.
/// There are no post-commit domain events: grants and the outbox row are written in the
/// same transaction as the transition, or the system can lose them (R5).
/// </para>
/// </summary>
public interface IOrderPaymentStateMachine
{
    Task<WebhookResult> ApplyAsync(
        int orderId,
        OrderStatus target,
        TapChargeSnapshot snapshot,
        PaymentSource source,
        long? webhookEventId,
        CancellationToken ct = default);
}

/// <summary>Refunds, from the admin or discovered from Tap's dashboard.</summary>
public interface IRefundService
{
    /// <summary>
    /// Refunds up to the amount still refundable. A refund revokes the order's download
    /// grants; a partial refund is driven by the running total, not by one refund's size.
    /// </summary>
    Task<(Refund? Refund, string? Problem)> RefundAsync(
        int orderId, decimal amountKwd, string reason, string? adminUserId, CancellationToken ct = default);
}
