using System.Globalization;
using Microsoft.Extensions.Options;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Accounts;

/// <summary>
/// Queues the two account emails.
/// <para>
/// <see cref="Delivery"/> is read from the transport configuration and never from whether
/// a row was written. That is deliberate: it is the value the endpoint reports back, and
/// deriving it from the enqueue would make it an answer to "does this address exist" for
/// anyone who tried a password reset.
/// </para>
/// </summary>
public sealed class AccountMailer(
    NexusDbContext db,
    IEmailOutbox outbox,
    IOptions<EmailOptions> options,
    TimeProvider clock)
{
    public MailDelivery Delivery => options.Value.EffectiveTransport() == EmailTransport.None
        ? MailDelivery.Suppressed
        : MailDelivery.Queued;

    public async Task QueueAsync(EmailKind kind, NexusUser user, string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(user.Email)) return;

        /* The key buckets by minute, so a customer who clicks "send it again" gets a
         * second message while a page that retries its own POST does not queue two. The
         * outbox treats a repeated key as already-queued rather than as an error. */
        var bucket = clock.GetUtcNow().ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);

        await outbox.EnqueueAsync(
            kind, orderId: null, customerId: user.CustomerId, toEmail: user.Email,
            language: language, idempotencyKey: $"{kind}:{user.Id}:{bucket}", ct);

        // The outbox writes into the ambient context and never saves; nothing else is
        // pending here, so this is the whole transaction.
        await db.SaveChangesAsync(ct);
    }
}
