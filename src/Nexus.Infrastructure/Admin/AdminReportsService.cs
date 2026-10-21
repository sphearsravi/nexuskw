using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexus.Domain;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Admin;

public sealed record SalesPoint(string Date, decimal RevenueKwd, int Orders);

public sealed record TitleSales(int BookId, string TitleEn, string TitleAr, int Copies, decimal RevenueKwd);

public sealed record UnsoldTitle(int BookId, string TitleEn, string TitleAr, decimal PriceKwd, bool IsPublished);

public sealed record RefundSummary(int Count, decimal TotalKwd, decimal AverageKwd, decimal RatePercent);

public sealed record LanguageSplit(int ArabicOrders, int EnglishOrders, decimal ArabicPercent);

public sealed record DeliverySummary(int GrantsIssued, int Downloads, int Exhausted, int Expired, int Revoked);

public sealed record CustomerSummary(int Total, int WithAccount, int Guests, int Repeat);

public sealed record ReportRange(string From, string To, int Days);

public sealed record StoreReport(
    ReportRange Range,
    decimal RevenueKwd,
    int PaidOrders,
    int AllOrders,
    decimal AverageOrderKwd,
    IReadOnlyList<SalesPoint> Daily,
    IReadOnlyList<TitleSales> BestSellers,
    IReadOnlyList<UnsoldTitle> NeverSold,
    RefundSummary Refunds,
    LanguageSplit Languages,
    DeliverySummary Delivery,
    CustomerSummary Customers);

/// <summary>
/// The reporting figures, counted from the database rather than estimated.
/// <para>
/// Two rules shape all of it. Revenue counts <b>captured</b> money, so a Pending order
/// contributes nothing — an operator who plans against orders that have not been paid
/// is planning against a number that can still go to zero. And every amount is KWD at
/// three decimals, because a report that rounds differently from the invoice is the
/// kind of discrepancy nobody can explain a month later.
/// </para>
/// </summary>
public sealed class AdminReportsService(NexusDbContext db)
{
    private static readonly OrderStatus[] Earned =
        [OrderStatus.Paid, OrderStatus.PartiallyRefunded, OrderStatus.Refunded];

    public async Task<StoreReport> BuildAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-days);

        var orders = await db.Orders
            .AsNoTracking()
            .Where(o => o.CreatedAt >= from)
            .Select(o => new OrderRow(
                o.Status, o.TotalCharged, o.RefundedTotalKwd, o.Language, o.CreatedAt, o.PaidAt))
            .ToListAsync(ct);

        var paid = orders.Where(o => Earned.Contains(o.Status)).ToList();

        // Net of refunds: money that came in and stayed in.
        var revenue = paid.Sum(o => o.TotalCharged - o.RefundedTotalKwd);

        var daily = BuildDailySeries(paid.Select(o => (o.PaidAt ?? o.CreatedAt, o.TotalCharged - o.RefundedTotalKwd)), from, days);

        return new StoreReport(
            new ReportRange(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), days),
            Currency.Kwd.Round(revenue),
            paid.Count,
            orders.Count,
            paid.Count == 0 ? 0m : Currency.Kwd.Round(revenue / paid.Count),
            daily,
            await BestSellersAsync(from, ct),
            await NeverSoldAsync(ct),
            Refunds(paid.Count, paid.Sum(o => o.RefundedTotalKwd), paid.Count(o => o.RefundedTotalKwd > 0)),
            Languages(paid),
            await DeliveryAsync(from, ct),
            await CustomersAsync(ct));
    }

    /// <summary>
    /// Every day in the range, including the empty ones. A series that skips quiet days
    /// draws a chart that flatters the shop.
    /// </summary>
    private static List<SalesPoint> BuildDailySeries(
        IEnumerable<(DateTimeOffset When, decimal Amount)> rows, DateTimeOffset from, int days)
    {
        var byDay = rows
            .GroupBy(r => r.When.UtcDateTime.Date)
            .ToDictionary(g => g.Key, g => (Revenue: g.Sum(x => x.Amount), Count: g.Count()));

        var series = new List<SalesPoint>(days);
        for (var i = 0; i < days; i++)
        {
            var day = from.UtcDateTime.Date.AddDays(i);
            byDay.TryGetValue(day, out var hit);
            series.Add(new SalesPoint(
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Currency.Kwd.Round(hit.Revenue),
                hit.Count));
        }
        return series;
    }

    /// <summary>
    /// Grouped in memory, deliberately. EF Core cannot translate a GroupBy that
    /// projects several aggregates alongside non-key columns across a join, and the
    /// row count here is small by design — a catalogue growing at about fifty titles a
    /// year, over one reporting window. Fighting the translator would buy nothing and
    /// cost a query nobody can read.
    /// </summary>
    private async Task<IReadOnlyList<TitleSales>> BestSellersAsync(DateTimeOffset from, CancellationToken ct)
    {
        var sold = await db.OrderItems
            .AsNoTracking()
            .Where(i => i.Order!.CreatedAt >= from && Earned.Contains(i.Order.Status))
            .Select(i => new ItemRow(i.BookId, i.TitleEnSnapshot, i.TitleArSnapshot, i.UnitPriceKwd))
            .ToListAsync(ct);

        return sold
            .GroupBy(i => i.BookId)
            .Select(g => new TitleSales(
                g.Key,
                g.First().TitleEn,
                g.First().TitleAr,
                g.Count(),
                Currency.Kwd.Round(g.Sum(x => x.UnitPriceKwd))))
            .OrderByDescending(t => t.Copies)
            .ThenBy(t => t.BookId)
            .Take(10)
            .ToList();
    }

    private sealed record ItemRow(int BookId, string TitleEn, string TitleAr, decimal UnitPriceKwd);

    /// <summary>
    /// Titles nobody has ever bought. The most actionable report a small catalogue has:
    /// it names what to reprice, re-describe or withdraw.
    /// </summary>
    private async Task<IReadOnlyList<UnsoldTitle>> NeverSoldAsync(CancellationToken ct)
    {
        var sold = db.OrderItems
            .Where(i => Earned.Contains(i.Order!.Status))
            .Select(i => i.BookId);

        return await db.Books
            .AsNoTracking()
            .Where(b => !sold.Contains(b.Id))
            .OrderBy(b => b.TitleEn)
            .Select(b => new UnsoldTitle(b.Id, b.TitleEn, b.TitleAr, b.PriceKwd, b.IsPublished))
            .Take(50)
            .ToListAsync(ct);
    }

    private static RefundSummary Refunds(int paidCount, decimal refunded, int refundedOrders) =>
        new(refundedOrders,
            Currency.Kwd.Round(refunded),
            refundedOrders == 0 ? 0m : Currency.Kwd.Round(refunded / refundedOrders),
            paidCount == 0 ? 0m : Math.Round(refundedOrders * 100m / paidCount, 1));

    /// <summary>
    /// The split the product exists to test: whether a Gulf reader actually wants this
    /// in Arabic. Counted from the language snapshotted on the order, not from the
    /// customer's current preference, which can change after the fact.
    /// </summary>
    private static LanguageSplit Languages(IReadOnlyCollection<OrderRow> paid)
    {
        var ar = paid.Count(o => o.Language == "ar");
        return new LanguageSplit(ar, paid.Count - ar,
            paid.Count == 0 ? 0m : Math.Round(ar * 100m / paid.Count, 1));
    }

    /// <summary>
    /// A named projection rather than an anonymous type. Anonymous types are internal,
    /// so passing one around as <c>dynamic</c> binds against a type the caller cannot
    /// see and throws at runtime — which is exactly what it did.
    /// </summary>
    private sealed record OrderRow(
        OrderStatus Status,
        decimal TotalCharged,
        decimal RefundedTotalKwd,
        string Language,
        DateTimeOffset CreatedAt,
        DateTimeOffset? PaidAt);

    private async Task<DeliverySummary> DeliveryAsync(DateTimeOffset from, CancellationToken ct)
    {
        var grants = await db.DownloadGrants.AsNoTracking()
            .Where(g => g.CreatedAt >= from)
            .Select(g => g.State)
            .ToListAsync(ct);

        var downloads = await db.DownloadLogs.AsNoTracking()
            .CountAsync(l => l.AtUtc >= from && l.Outcome == DownloadOutcome.Served, ct);

        return new DeliverySummary(
            grants.Count,
            downloads,
            grants.Count(s => s == GrantState.Exhausted),
            grants.Count(s => s == GrantState.Expired),
            grants.Count(s => s == GrantState.Revoked));
    }

    private async Task<CustomerSummary> CustomersAsync(CancellationToken ct)
    {
        var total = await db.Customers.AsNoTracking().CountAsync(ct);
        var withAccount = await db.Customers.AsNoTracking().CountAsync(c => c.IdentityUserId != null, ct);

        // Counted in memory for the same reason as the best sellers: a GroupBy with a
        // filtered aggregate is not reliably translatable, and this set is small.
        var buyers = await db.Orders.AsNoTracking()
            .Where(o => Earned.Contains(o.Status))
            .Select(o => o.CustomerId)
            .ToListAsync(ct);

        var repeat = buyers.GroupBy(id => id).Count(g => g.Count() > 1);

        return new CustomerSummary(total, withAccount, total - withAccount, repeat);
    }

    /// <summary>
    /// The daily series as CSV, for a spreadsheet. Invariant culture and a quoted,
    /// escaped writer: an operator's machine may well use a comma as its decimal
    /// separator, and a report that silently reshapes itself per locale is worse than
    /// no report.
    /// </summary>
    public static string ToCsv(StoreReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("date,revenue_kwd,orders");
        foreach (var p in report.Daily)
            sb.Append(p.Date).Append(',')
              .Append(p.RevenueKwd.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
              .Append(p.Orders.ToString(CultureInfo.InvariantCulture)).AppendLine();

        sb.AppendLine();
        sb.AppendLine("best_sellers");
        sb.AppendLine("book_id,title_en,title_ar,copies,revenue_kwd");
        foreach (var t in report.BestSellers)
            sb.Append(t.BookId).Append(',')
              .Append(Csv(t.TitleEn)).Append(',')
              .Append(Csv(t.TitleAr)).Append(',')
              .Append(t.Copies).Append(',')
              .Append(t.RevenueKwd.ToString("F3", CultureInfo.InvariantCulture)).AppendLine();

        return sb.ToString();
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? '"' + value.Replace("\"", "\"\"") + '"'
            : value;
}
