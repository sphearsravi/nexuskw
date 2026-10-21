using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexus.Application.Common;
using Nexus.Application.Email;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Email;

/// <summary>A grant that cannot carry a resend, and the book it belongs to.</summary>
public sealed record DeadGrant(int BookId, string TitleEn, string TitleAr, string State);

public sealed record ResendRequest(
    int OrderId,
    string? Language = null,
    string? ToOverride = null,
    string? ActorId = null,
    string? ActorEmail = null,
    string? IpAddress = null);

public sealed record ResendOutcome(
    bool Queued,
    string? Problem = null,
    string? PublicRef = null,
    string? To = null,
    string? Language = null,
    int? RetryAfterSeconds = null,
    int? Limit = null,
    string? Scope = null,
    IReadOnlyList<DeadGrant>? DeadGrants = null);

/// <summary>
/// The richer resend the admin endpoint needs. <see cref="IOrderEmailService"/> is the
/// frozen contract the payment path calls; this adds the language and recipient overrides
/// an operator fixing a typo'd address actually uses, without widening that contract.
/// </summary>
public interface IAdminEmailOperations
{
    Task<ResendOutcome> ResendAsync(ResendRequest request, CancellationToken ct = default);
}

/// <summary>
/// The order-facing reasons to send mail.
/// <para>
/// Enqueue does the minimum possible inside the caller's transaction: one indexed seek and
/// one insert. No Setting is read, no template is loaded and nothing is rendered — all of
/// that is the dispatcher's work, because composing an email inside the payment
/// transaction is a way to roll back a captured payment.
/// </para>
/// </summary>
public sealed partial class OrderEmailService(
    NexusDbContext db,
    IEmailOutbox outbox,
    ISettingsReader settings,
    IEmailSuppressionList suppressions,
    IOptions<EmailOptions> options,
    ILogger<OrderEmailService> log,
    TimeProvider clock) : IOrderEmailService, IAdminEmailOperations
{
    public async Task QueueOrderConfirmationAsync(int orderId, CancellationToken ct = default)
    {
        try
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null)
            {
                LogNoOrder(log, orderId);
                return;
            }

            if (order.Status is OrderStatus.Pending or OrderStatus.Failed or OrderStatus.Cancelled)
            {
                LogNotPayable(log, order.OrderNumber, order.Status);
                return;
            }

            await outbox.EnqueueAsync(
                EmailKind.OrderConfirmation, order.Id, order.CustomerId,
                order.BuyerEmailSnapshot, order.Language,
                $"order-confirmation:{order.Id}", ct);
        }
        catch (Exception ex)
        {
            /* Never rethrown. The caller is inside the transaction that captured money; an
             * exception from here would roll back a paid order and the customer would have
             * been charged for nothing. The failure is recorded where an operator can see
             * it instead. */
            LogEnqueueFailed(log, orderId, ex);
            TryRecordFailure(orderId, EmailKind.OrderConfirmation, ex);
        }
    }

    public async Task QueueRefundNoticeAsync(int orderId, decimal amountKwd, CancellationToken ct = default)
    {
        try
        {
            var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null)
            {
                LogNoOrder(log, orderId);
                return;
            }

            /* Keyed on the running total as well as this refund's amount: a replayed refund
             * webhook sees the same running total and collapses onto the same row, while a
             * genuine second partial refund of the same amount has moved the total on and
             * gets its own notice. */
            var key = string.Create(CultureInfo.InvariantCulture,
                $"refund-notice:{order.Id}:{Currency.Kwd.Format(amountKwd)}:{Currency.Kwd.Format(order.RefundedTotalKwd)}");

            await outbox.EnqueueAsync(
                EmailKind.RefundNotice, order.Id, order.CustomerId,
                order.BuyerEmailSnapshot, order.Language, key, ct);
        }
        catch (Exception ex)
        {
            LogEnqueueFailed(log, orderId, ex);
            TryRecordFailure(orderId, EmailKind.RefundNotice, ex);
        }
    }

    public async Task<(bool Queued, string? Problem)> ResendDownloadLinksAsync(
        int orderId, string? adminUserId, CancellationToken ct = default)
    {
        var outcome = await ResendAsync(new ResendRequest(orderId, ActorId: adminUserId), ct);
        return (outcome.Queued, outcome.Problem);
    }

    public async Task<ResendOutcome> ResendAsync(ResendRequest request, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var order = await db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);

        if (order is null)
            return await RefuseAsync(request, "order_not_found", now, ct);

        if (order.Status is not (OrderStatus.Paid or OrderStatus.PartiallyRefunded))
            return await RefuseAsync(request, "order_not_paid", now, ct);

        if (request.Language is not null && !Lang.TryNormalise(request.Language, out _))
            return await RefuseAsync(request, "invalid_language", now, ct);

        var language = Lang.Normalise(request.Language ?? order.Language);
        var recipient = (request.ToOverride ?? order.BuyerEmailSnapshot).Trim();

        if (!IsPlausibleAddress(recipient))
            return await RefuseAsync(request, "invalid_recipient", now, ct);

        if (options.Value.EffectiveTransport() == EmailTransport.None)
            return await RefuseAsync(request, "email_disabled", now, ct);

        if (await suppressions.IsSuppressedAsync(recipient, ct))
            return await RefuseAsync(request, "recipient_suppressed", now, ct);

        var dead = await DeadGrantsAsync(order, now, ct);
        if (dead.Live == 0)
        {
            var refusal = await RefuseAsync(request, "no_live_grants", now, ct);
            return refusal with { DeadGrants = dead.Dead };
        }

        if (await RateLimitAsync(order.Id, recipient, request.ActorId, now, ct) is { } limited)
        {
            var refusal = await RefuseAsync(request, "rate_limited", now, ct);
            return refusal with
            {
                Scope = limited.Scope,
                Limit = limited.Limit,
                RetryAfterSeconds = limited.RetryAfterSeconds
            };
        }

        /* A resend is meant to be a new email, so the key carries the sequence rather than
         * collapsing onto the first one. What bounds it is the rate limit; the key is still
         * unique so a double-clicked button in the same second collides rather than sending
         * twice. */
        var priorCount = await db.OutboxEmails
            .CountAsync(e => e.OrderId == order.Id && e.Kind == EmailKind.DownloadLinks, ct);

        var row = await outbox.EnqueueAsync(
            EmailKind.DownloadLinks, order.Id, order.CustomerId, recipient, language,
            $"download-links:{order.Id}:{priorCount}", ct);

        await AuditAsync(request, "email.resend.queued", now, new { row.PublicRef, To = recipient, language }, ct);

        // Outside any payment transaction, so this one does save: the admin call has no
        // caller to commit on its behalf.
        await db.SaveChangesAsync(ct);

        return new ResendOutcome(true, null, row.PublicRef, recipient, language);
    }

    private sealed record Limited(string Scope, int Limit, int RetryAfterSeconds);

    private async Task<Limited?> RateLimitAsync(
        int orderId, string recipient, string? actorId, DateTimeOffset now, CancellationToken ct)
    {
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddDays(-1);
        var normalisedRecipient = EmailSuppressionList.Normalise(recipient);

        var perOrderHour = await settings.GetIntAsync(EmailSettingKeys.ResendPerOrderPerHour, 3, 1, 500, ct);
        var perOrderDay = await settings.GetIntAsync(SettingKeys.ResendLimitPerDay, 10, 1, 500, ct);
        var perRecipientHour = await settings.GetIntAsync(EmailSettingKeys.ResendPerRecipientPerHour, 5, 1, 500, ct);
        var perRecipientDay = await settings.GetIntAsync(EmailSettingKeys.ResendPerRecipientPerDay, 20, 1, 500, ct);
        var perActorHour = await settings.GetIntAsync(EmailSettingKeys.ResendPerActorPerHour, 30, 1, 500, ct);

        // Counted from rows, never an in-memory counter: this app pool recycles often, and
        // a limit that resets on recycle is not a limit.
        var orderHour = await db.OutboxEmails.CountAsync(
            e => e.OrderId == orderId && e.Kind == EmailKind.DownloadLinks && e.CreatedAt >= hourAgo, ct);
        if (orderHour >= perOrderHour) return new("order", perOrderHour, 3600);

        var orderDay = await db.OutboxEmails.CountAsync(
            e => e.OrderId == orderId && e.Kind == EmailKind.DownloadLinks && e.CreatedAt >= dayAgo, ct);
        if (orderDay >= perOrderDay) return new("order", perOrderDay, 86400);

        // Across all orders and all actors: the asset being protected is the sending
        // domain's reputation, and one address receiving twenty of our emails in an hour
        // burns it just as well as twenty addresses receiving one.
        var recipientHour = await db.OutboxEmails.CountAsync(
            e => e.ToEmail.ToLower() == normalisedRecipient && e.CreatedAt >= hourAgo, ct);
        if (recipientHour >= perRecipientHour) return new("recipient", perRecipientHour, 3600);

        var recipientDay = await db.OutboxEmails.CountAsync(
            e => e.ToEmail.ToLower() == normalisedRecipient && e.CreatedAt >= dayAgo, ct);
        if (recipientDay >= perRecipientDay) return new("recipient", perRecipientDay, 86400);

        if (!string.IsNullOrEmpty(actorId))
        {
            // From the audit log, not the outbox, so a refused call still counts. A wall of
            // refusals is exactly the signal the limit exists to catch, and a refusal that
            // costs the attacker nothing is not a limit either.
            var actorHour = await db.AuditLogs.CountAsync(
                a => a.ActorId == actorId && a.Entity == "OutboxEmail" && a.At >= hourAgo, ct);
            if (actorHour >= perActorHour) return new("actor", perActorHour, 3600);
        }

        return null;
    }

    private async Task<(int Live, IReadOnlyList<DeadGrant> Dead)> DeadGrantsAsync(
        Order order, DateTimeOffset now, CancellationToken ct)
    {
        var grants = await db.DownloadGrants
            .AsNoTracking()
            .Where(g => g.OrderId == order.Id)
            .ToListAsync(ct);

        var live = grants.Count(g => g.ServeBlocker(now) is null);

        var dead = grants
            .Where(g => g.ServeBlocker(now) is not null)
            .GroupBy(g => g.BookId)
            .Select(g => g.OrderByDescending(x => x.Sequence).First())
            .Select(g =>
            {
                var item = order.Items.FirstOrDefault(i => i.BookId == g.BookId);
                return new DeadGrant(
                    g.BookId,
                    item?.TitleEnSnapshot ?? "",
                    item?.TitleArSnapshot ?? "",
                    g.ServeBlocker(now) ?? "unknown");
            })
            .ToList();

        return (live, dead);
    }

    private async Task<ResendOutcome> RefuseAsync(
        ResendRequest request, string problem, DateTimeOffset now, CancellationToken ct)
    {
        await AuditAsync(request, "email.resend.refused", now, new { problem }, ct);
        await db.SaveChangesAsync(ct);
        LogResendRefused(log, request.OrderId, problem, request.ActorId ?? "unknown");
        return new ResendOutcome(false, problem);
    }

    private Task AuditAsync(
        ResendRequest request, string action, DateTimeOffset now, object? after, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = request.ActorId,
            ActorEmail = request.ActorEmail,
            Entity = "OutboxEmail",
            EntityId = request.OrderId.ToString(CultureInfo.InvariantCulture),
            Action = action,
            AfterJson = after is null ? null : JsonSerializer.Serialize(after),
            At = now,
            IpAddress = request.IpAddress
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deliberately shallow. The mail server decides what a real address is; this only
    /// stops an obvious typo or a header injection reaching the envelope.
    /// </summary>
    public static bool IsPlausibleAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Any(c => c is '\r' or '\n' or ',' or ';' or '<' or '>')) return false;

        var at = value.IndexOf('@');
        return at > 0
            && at == value.LastIndexOf('@')
            && at < value.Length - 1
            && value[(at + 1)..].Contains('.');
    }

    /// <summary>
    /// A last resort: the enqueue itself threw. The row is added but not saved, so it lives
    /// or dies with the caller's transaction exactly as a successful enqueue would.
    /// </summary>
    private void TryRecordFailure(int orderId, EmailKind kind, Exception cause)
    {
        try
        {
            var publicRef = EmailOutbox.NewPublicRef();
            db.OutboxEmails.Add(new OutboxEmail
            {
                Kind = kind,
                OrderId = orderId,
                ToEmail = "unknown@nexus.invalid",
                Language = Lang.En,
                IdempotencyKey = $"failed:{kind}:{orderId}:{publicRef}",
                PublicRef = publicRef,
                Status = EmailStatus.Failed,
                LastError = $"enqueue threw: {cause.GetType().Name}: {cause.Message}",
                CreatedAt = clock.GetUtcNow()
            });
        }
        catch (Exception ex)
        {
            LogFailureRowFailed(log, orderId, ex);
        }
    }

    [LoggerMessage(EventId = 5170, Level = LogLevel.Error, Message = "No order {OrderId} to email about.")]
    private static partial void LogNoOrder(ILogger logger, int orderId);

    [LoggerMessage(EventId = 5171, Level = LogLevel.Warning,
        Message = "Order {OrderNumber} is {Status}; no confirmation queued.")]
    private static partial void LogNotPayable(ILogger logger, string orderNumber, OrderStatus status);

    [LoggerMessage(EventId = 5172, Level = LogLevel.Error,
        Message = "Queueing email for order {OrderId} threw. The payment transaction is unaffected.")]
    private static partial void LogEnqueueFailed(ILogger logger, int orderId, Exception exception);

    [LoggerMessage(EventId = 5173, Level = LogLevel.Error,
        Message = "Could not even record the failed enqueue for order {OrderId}.")]
    private static partial void LogFailureRowFailed(ILogger logger, int orderId, Exception exception);

    [LoggerMessage(EventId = 5174, Level = LogLevel.Information,
        Message = "Resend for order {OrderId} refused ({Problem}) for actor {Actor}.")]
    private static partial void LogResendRefused(ILogger logger, int orderId, string problem, string actor);
}
