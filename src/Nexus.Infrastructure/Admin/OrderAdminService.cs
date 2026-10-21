using Microsoft.EntityFrameworkCore;
using Nexus.Application.Common;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// Orders, as the operator reads them and acts on them.
/// <para>
/// Everything a human is shown comes from the <b>snapshot on the order</b>, never from the
/// customer row: that row is mutable and, for a guest checkout, writable from an
/// unauthenticated request. An invoice reprinted next year has to say what was charged to
/// whom at the time.
/// </para>
/// </summary>
public sealed class OrderAdminService(NexusDbContext db, IAuditLog audit)
{
    public const int MaxPageSize = 100;

    public async Task<Page<AdminOrderRow>> ListAsync(
        string? status, string? q, int page, int pageSize, CancellationToken ct = default)
    {
        var query = db.Orders.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var wanted))
            query = query.Where(o => o.Status == wanted);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            query = query.Where(o =>
                o.OrderNumber.Contains(needle) ||
                o.BuyerEmailSnapshot.Contains(needle) ||
                o.BuyerNameSnapshot.Contains(needle) ||
                (o.TapChargeId != null && o.TapChargeId.Contains(needle)) ||
                (o.TapGatewayReference != null && o.TapGatewayReference.Contains(needle)));
        }

        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        var skip = Math.Max(0, page - 1) * size;

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Skip(skip).Take(size)
            .Select(o => new AdminOrderRow(
                o.Id, o.OrderNumber, o.BuyerNameSnapshot, o.BuyerEmailSnapshot,
                o.Status.ToString(), o.Language, o.TotalKwd, o.RefundedTotalKwd,
                o.Items.Count, o.PaymentMethod, o.CreatedAt, o.PaidAt))
            .ToListAsync(ct);

        return new Page<AdminOrderRow>(total, Math.Max(1, page), size, rows);
    }

    public async Task<AdminOrderDetail?> GetAsync(int id, CancellationToken ct = default)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        if (order is null) return null;

        var grants = await db.DownloadGrants.AsNoTracking()
            .Where(g => g.OrderId == id)
            .Include(g => g.Book)
            .OrderBy(g => g.BookId).ThenBy(g => g.Sequence)
            .Select(g => new AdminOrderGrantRow(
                g.Id, g.BookId,
                g.Book!.TitleEn, g.Book.TitleAr,
                g.Sequence, g.UsedCount, g.MaxDownloads, g.State.ToString(), g.ExpiresAt))
            .ToListAsync(ct);

        var emails = await db.OutboxEmails.AsNoTracking()
            .Where(e => e.OrderId == id)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new AdminOrderEmailRow(
                e.PublicRef, e.Kind.ToString(), e.Language, e.ToEmail, e.Status.ToString(),
                e.AttemptCount, e.CreatedAt, e.SentAt, e.LastError))
            .ToListAsync(ct);

        var events = await db.OrderEvents.AsNoTracking()
            .Where(e => e.OrderId == id)
            .OrderBy(e => e.At)
            .Select(e => new AdminOrderEventRow(
                e.FromStatus == null ? null : e.FromStatus.ToString(),
                e.ToStatus.ToString(), e.Source.ToString(), e.Detail, e.At))
            .ToListAsync(ct);

        var items = order.Items
            .Select(i => new AdminOrderItemRow(
                i.BookId, i.TitleEnSnapshot, i.TitleArSnapshot,
                i.AuthorEnSnapshot, i.AuthorArSnapshot, i.UnitPriceKwd))
            .ToList();

        return new AdminOrderDetail(
            Row(order), order.CustomerId, order.StatusDetail, order.ReviewReason,
            order.SubtotalKwd, order.TotalCharged,
            order.TapChargeId, order.TapPaymentReference, order.TapGatewayReference,
            items, grants, emails, events);
    }

    /// <summary>
    /// <b>Refunds are exposed and refused, not faked.</b> The gateway integration is held
    /// pending a Tap sandbox sample, and no order can be Paid yet. Writing a Refund row
    /// and revoking the grants without money moving would put the books out of reach of a
    /// customer who has not been repaid — a worse outcome than an honest refusal, and one
    /// the audit trail would then assert was a real refund.
    /// </summary>
    public async Task<AdminProblem> RefundAsync(
        int id, decimal amountKwd, string? reason, CancellationToken ct = default)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return new AdminProblem("order_not_found");

        if (order.Status is not (OrderStatus.Paid or OrderStatus.PartiallyRefunded))
            return new AdminProblem("order_not_paid", order.Status.ToString());

        if (amountKwd <= 0m || amountKwd > order.RefundCeiling())
            return new AdminProblem("refund_amount_out_of_range",
                Currency.Kwd.Format(order.RefundCeiling()));

        // The attempt is recorded even though nothing moves: "we tried to refund this and
        // the gateway was not live" is exactly the question asked later.
        await audit.RecordAsync("Order", order.Id, "order.refund.refused", null, new
        {
            Amount = Currency.Kwd.Format(amountKwd),
            Reason = reason,
            Problem = "payments_held"
        }, ct);

        await db.SaveChangesAsync(ct);
        return new AdminProblem("payments_held");
    }

    private static AdminOrderRow Row(Order o) => new(
        o.Id, o.OrderNumber, o.BuyerNameSnapshot, o.BuyerEmailSnapshot,
        o.Status.ToString(), o.Language, o.TotalKwd, o.RefundedTotalKwd,
        o.Items.Count, o.PaymentMethod, o.CreatedAt, o.PaidAt);
}
