using Nexus.Domain;
using Nexus.Domain.Entities;

namespace Nexus.Application.Email;

public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>A message handed to a transport. The body is produced at send, never stored.</summary>
public sealed record OutgoingEmail(
    string ToEmail,
    string ToName,
    string Subject,
    string Html,
    string Text,
    string MessageId,
    string? ReturnPath);

public enum EmailSendOutcome
{
    Sent = 0,
    /// <summary>Temporary: a greylist, a timeout, a 4xx. Worth retrying.</summary>
    TransientFailure = 1,
    /// <summary>Permanent: no such mailbox, blocked. Retrying only makes it worse.</summary>
    PermanentFailure = 2
}

public sealed record EmailSendResult(EmailSendOutcome Outcome, string? Detail, string? Classification);

/// <summary>
/// Puts a message on the queue. Nothing sends inside a web request: Tap expects a fast
/// 200 from the webhook, and an SMTP timeout must never be the reason an order fails to
/// complete.
/// </summary>
public interface IEmailOutbox
{
    /// <summary>
    /// Enqueues, inside the caller's transaction. <paramref name="idempotencyKey"/> makes
    /// this exactly-once: one paid order sends one email however many times Tap retries
    /// the webhook.
    /// </summary>
    Task<OutboxEmail> EnqueueAsync(
        EmailKind kind, int? orderId, int? customerId, string toEmail, string language,
        string idempotencyKey, CancellationToken ct = default);
}

/// <summary>
/// Renders a message at dispatch time from the order, never from a stored body.
/// <para>
/// This is why the outbox keeps no body: a retry eight days later re-reads the grants and
/// is therefore either correct or honestly refused, rather than sending a link that
/// expired while the message sat in the queue. It also keeps live download tokens out of
/// the database and out of every backup.
/// </para>
/// </summary>
public interface IEmailTemplateRenderer
{
    /// <summary>
    /// Renders in <paramref name="language"/>, right-to-left for Arabic. Money is KWD at
    /// three decimals in Western digits in both languages.
    /// </summary>
    Task<(RenderedEmail? Email, string? Problem)> RenderAsync(
        OutboxEmail queued, CancellationToken ct = default);
}

/// <summary>A transport. Implementations: a file drop for development, SMTP for production, and none.</summary>
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(OutgoingEmail message, CancellationToken ct = default);
}

/// <summary>Addresses we must stop writing to.</summary>
public interface IEmailSuppressionList
{
    Task<bool> IsSuppressedAsync(string email, CancellationToken ct = default);

    Task SuppressAsync(
        string email, SuppressionReason reason, string? classification, CancellationToken ct = default);
}

/// <summary>The order-facing reasons to send mail.</summary>
public interface IOrderEmailService
{
    /// <summary>
    /// Queues the confirmation and the download links for a paid order. Called from inside
    /// the payment transaction, so a paid order cannot exist without its email queued.
    /// </summary>
    Task QueueOrderConfirmationAsync(int orderId, CancellationToken ct = default);

    Task QueueRefundNoticeAsync(int orderId, decimal amountKwd, CancellationToken ct = default);

    /// <summary>
    /// Re-sends the download links on operator request. Rate-limited: this is the fix for
    /// the commonest support call, not a way to mail an address repeatedly.
    /// </summary>
    Task<(bool Queued, string? Problem)> ResendDownloadLinksAsync(
        int orderId, string? adminUserId, CancellationToken ct = default);
}
