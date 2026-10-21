using Microsoft.EntityFrameworkCore;
using Nexus.Domain;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// The dashboard figures, every one of them counted from the database.
/// <para>
/// The demo multiplied a sample total by 6.4 to make the panel look busy. Nothing here
/// does: payments are held, so revenue is genuinely zero until the first charge settles,
/// and a dashboard that invents a number is worse than one that reads nothing — the
/// operator cannot tell which of the two it is doing.
/// </para>
/// </summary>
public sealed class AdminDashboardService(NexusDbContext db, TimeProvider clock)
{
    public const int Window = 30;

    public async Task<DashboardSummary> SummariseAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var from = now.AddDays(-Window);

        // Captured minus refunded, over orders that actually took money. A Pending order
        // is not revenue, and a refunded one stopped being revenue.
        var earning = db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.Paid || o.Status == OrderStatus.PartiallyRefunded);

        var paidWindow = earning.Where(o => o.PaidAt != null && o.PaidAt >= from);

        var revenue = await paidWindow.SumAsync(o => (decimal?)(o.TotalCharged - o.RefundedTotalKwd), ct) ?? 0m;
        var refunded = await paidWindow.SumAsync(o => (decimal?)o.RefundedTotalKwd, ct) ?? 0m;
        var paidOrders = await paidWindow.CountAsync(ct);

        var orders30 = await db.Orders.AsNoTracking().CountAsync(o => o.CreatedAt >= from, ct);
        var pending = await db.Orders.AsNoTracking().CountAsync(o => o.Status == OrderStatus.Pending, ct);

        // Served, not attempted: a refused attempt is a support problem, not a delivery.
        var downloads30 = await db.DownloadLogs.AsNoTracking()
            .CountAsync(l => l.AtUtc >= from && l.Outcome == DownloadOutcome.Served, ct);

        var titlesLive = await db.Books.AsNoTracking().CountAsync(b => b.IsPublished, ct);
        var titlesTotal = await db.Books.AsNoTracking().CountAsync(ct);
        var customers = await db.Customers.AsNoTracking().CountAsync(ct);

        return new DashboardSummary(
            Currency.Kwd.Round(revenue),
            Currency.Kwd.Round(refunded),
            orders30,
            paidOrders,
            downloads30,
            titlesLive,
            titlesTotal,
            pending,
            customers,
            await TrendAsync(from, now, ct),
            await TopTitlesAsync(from, ct),
            await RecentAsync(ct));
    }

    /// <summary>
    /// Thirty rows, always, zeros included. A sparse series makes the chart lie about the
    /// shape of a quiet week by closing the gaps.
    /// </summary>
    private async Task<IReadOnlyList<DashboardDay>> TrendAsync(
        DateTimeOffset from, DateTimeOffset now, CancellationToken ct)
    {
        // Grouped in memory on purpose. Thirty days of a store this size is a handful of
        // rows, and grouping by a date server-side means trusting the provider's idea of a
        // DateTimeOffset's calendar day, which is not the same question as "which UTC day".
        var paid = await db.Orders.AsNoTracking()
            .Where(o => o.PaidAt != null && o.PaidAt >= from &&
                        (o.Status == OrderStatus.Paid || o.Status == OrderStatus.PartiallyRefunded))
            .Select(o => new { At = o.PaidAt!.Value, Net = o.TotalCharged - o.RefundedTotalKwd })
            .ToListAsync(ct);

        var served = await db.DownloadLogs.AsNoTracking()
            .Where(l => l.AtUtc >= from && l.Outcome == DownloadOutcome.Served)
            .Select(l => l.AtUtc)
            .ToListAsync(ct);

        var days = new List<DashboardDay>(Window);
        for (var i = Window - 1; i >= 0; i--)
        {
            var date = DateOnly.FromDateTime(now.AddDays(-i).UtcDateTime);
            var sameDay = paid.Where(p => DateOnly.FromDateTime(p.At.UtcDateTime) == date).ToList();

            days.Add(new DashboardDay(
                date,
                Currency.Kwd.Round(sameDay.Sum(p => p.Net)),
                sameDay.Count,
                served.Count(s => DateOnly.FromDateTime(s.UtcDateTime) == date)));
        }

        return days;
    }

    /// <summary>
    /// Ranked by copies actually sold in the window, not by <c>Book.SalesCount</c>, which
    /// carries the seeded demo figures and is a lifetime number besides.
    /// </summary>
    private async Task<IReadOnlyList<DashboardTopTitle>> TopTitlesAsync(
        DateTimeOffset from, CancellationToken ct)
    {
        // Grouped in memory: a GroupBy over a joined navigation does not translate, and
        // thirty days of items for a store this size is a short list. The filter is the
        // part that has to run on the server, and it does.
        var sold = await db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.PaidAt != null && i.Order.PaidAt >= from &&
                        (i.Order.Status == OrderStatus.Paid || i.Order.Status == OrderStatus.PartiallyRefunded))
            .Select(i => new { i.BookId, i.TitleEnSnapshot, i.TitleArSnapshot, i.UnitPriceKwd })
            .ToListAsync(ct);

        return sold
            .GroupBy(i => new { i.BookId, i.TitleEnSnapshot, i.TitleArSnapshot })
            .Select(g => new DashboardTopTitle(
                g.Key.BookId, g.Key.TitleEnSnapshot, g.Key.TitleArSnapshot,
                g.Count(), Currency.Kwd.Round(g.Sum(i => i.UnitPriceKwd))))
            .OrderByDescending(t => t.Copies)
            .Take(6)
            .ToList();
    }

    private async Task<IReadOnlyList<AdminOrderRow>> RecentAsync(CancellationToken ct) =>
        await db.Orders.AsNoTracking()
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id)
            .Take(5)
            .Select(o => new AdminOrderRow(
                o.Id, o.OrderNumber, o.BuyerNameSnapshot, o.BuyerEmailSnapshot,
                o.Status.ToString(), o.Language, o.TotalKwd, o.RefundedTotalKwd,
                o.Items.Count, o.PaymentMethod, o.CreatedAt, o.PaidAt))
            .ToListAsync(ct);
}
