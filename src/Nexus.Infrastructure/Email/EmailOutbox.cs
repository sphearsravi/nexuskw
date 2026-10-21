using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Email;

/// <summary>
/// The transactional outbox.
/// <para>
/// <b>It never calls <c>SaveChanges</c>.</b> It adds to the ambient context and returns;
/// the caller's transaction decides whether the row exists. That is the one rule that makes
/// the whole design true — an order cannot be paid with no email queued, and a payment that
/// rolls back cannot leave an email queued for an order that never completed — and it is
/// the easiest to undo by accident, because a helpful <c>SaveChangesAsync</c> here looks
/// like a fix.
/// </para>
/// </summary>
public sealed partial class EmailOutbox(
    NexusDbContext db,
    IEmailSuppressionList suppressions,
    IOptions<EmailOptions> options,
    ILogger<EmailOutbox> log,
    TimeProvider clock) : IEmailOutbox
{
    public async Task<OutboxEmail> EnqueueAsync(
        EmailKind kind, int? orderId, int? customerId, string toEmail, string language,
        string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(toEmail);

        if (await FindExistingAsync(idempotencyKey, ct) is { } existing)
        {
            // Not an error. The caller is usually Tap's webhook being retried, and throwing
            // would turn a successful duplicate suppression into a 500 and another retry.
            LogAlreadyQueued(log, idempotencyKey, existing.PublicRef);
            return existing;
        }

        if (!Lang.TryNormalise(language, out var normalisedLanguage))
            LogUnknownLanguage(log, language, normalisedLanguage);

        var config = options.Value;
        var now = clock.GetUtcNow();
        var publicRef = NewPublicRef();

        var row = new OutboxEmail
        {
            Kind = kind,
            OrderId = orderId,
            CustomerId = customerId,
            ToEmail = toEmail.Trim(),
            Language = normalisedLanguage,
            IdempotencyKey = idempotencyKey,
            PublicRef = publicRef,
            MessageId = VerpAddress.MessageId(publicRef, config.BounceDomain),
            Status = EmailStatus.Queued,
            NextAttemptAt = now,
            CreatedAt = now
        };

        /* Both of these write the row and mark it Suppressed rather than skipping it. An
         * operator must be able to see that this customer was not emailed; a missing row is
         * indistinguishable from a bug. Neither reads a Setting or renders anything — the
         * whole point of enqueue is that it is one insert and one indexed seek inside
         * somebody else's payment transaction. */
        if (config.EffectiveTransport() == EmailTransport.None)
        {
            row.Status = EmailStatus.Suppressed;
            row.LastError = "sending disabled: Email:Transport resolves to None";
            row.NextAttemptAt = null;
        }
        else if (await suppressions.IsSuppressedAsync(row.ToEmail, ct))
        {
            row.Status = EmailStatus.Suppressed;
            row.LastError = "recipient suppressed";
            row.NextAttemptAt = null;
        }

        db.OutboxEmails.Add(row);
        return row;
    }

    /// <summary>
    /// The change tracker as well as the table: inside one transaction the first enqueue
    /// has not been saved yet, so a second call for the same key would otherwise insert a
    /// duplicate and fail at commit on the unique index.
    /// </summary>
    private async Task<OutboxEmail?> FindExistingAsync(string idempotencyKey, CancellationToken ct)
    {
        var pending = db.ChangeTracker.Entries<OutboxEmail>()
            .Select(e => e.Entity)
            .FirstOrDefault(e => e.IdempotencyKey == idempotencyKey);

        return pending ?? await db.OutboxEmails
            .FirstOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey, ct);
    }

    /// <summary>
    /// Base64url of a GUID: 22 characters, no padding. The only identifier that leaves the
    /// server, so it discloses nothing about how many emails the store has sent.
    /// </summary>
    public static string NewPublicRef() =>
        Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    [LoggerMessage(EventId = 5110, Level = LogLevel.Information,
        Message = "Outbox idempotency key {Key} is already queued as {PublicRef}; nothing added.")]
    private static partial void LogAlreadyQueued(ILogger logger, string key, string publicRef);

    [LoggerMessage(EventId = 5111, Level = LogLevel.Warning,
        Message = "Unrecognised email language {Requested}; falling back to {Language}.")]
    private static partial void LogUnknownLanguage(ILogger logger, string requested, string language);
}
