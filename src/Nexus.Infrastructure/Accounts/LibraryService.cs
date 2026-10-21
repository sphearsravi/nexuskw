using Microsoft.EntityFrameworkCore;
using Nexus.Application.Delivery;
using Nexus.Domain;
using Nexus.Infrastructure.Email;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Accounts;

/// <summary>
/// AC-3 and AC-5: what a signed-in customer owns, and what they were charged.
/// <para>
/// <b>Every method takes the customer id and filters on it in the query.</b> None takes
/// an order id or a grant id on its own. An authorisation check that happens after the
/// row is loaded is one <c>return</c> away from not happening, so there is no shape here
/// that can load somebody else's row in the first place.
/// </para>
/// <para>
/// The library is read from <c>DownloadGrant</c>, not from the browser. The page used to
/// read <c>localStorage</c>, which meant the shelf was empty on a second device, full of
/// books on a shared computer, and wrong about the download counter in both cases.
/// </para>
/// </summary>
public sealed class LibraryService(
    NexusDbContext db,
    IDeliveryService delivery,
    TimeProvider clock)
{
    /// <summary>The landing page, never <c>/download/{token}</c>: a link checker must not spend a download.</summary>
    public const string DownloadPathPrefix = "/download.html#";

    /// <summary>Only a paid order delivers. A refunded one keeps its invoice and loses its grants.</summary>
    private static readonly OrderStatus[] Delivering = [OrderStatus.Paid, OrderStatus.PartiallyRefunded];

    public async Task<IReadOnlyList<LibraryItem>> LibraryAsync(int customerId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();

        var rows = await db.DownloadGrants
            .AsNoTracking()
            .Where(g => g.Order!.CustomerId == customerId && Delivering.Contains(g.Order!.Status))
            .Select(g => new
            {
                Grant = g,
                g.Order!.OrderNumber,
                PurchasedAt = g.Order!.PaidAt ?? g.Order!.CreatedAt,
                Item = g.Order!.Items.FirstOrDefault(i => i.BookId == g.BookId)
            })
            .ToListAsync(ct);

        /* One row per book: a re-issue supersedes the grant it replaced and both stay in
         * the table, so the highest sequence is the live one and the rest are history.
         * Showing both would offer the customer a link that is dead by design. */
        var newest = rows
            .GroupBy(r => (r.Grant.OrderId, r.Grant.BookId))
            .Select(g => g.OrderByDescending(r => r.Grant.Sequence).First())
            .OrderByDescending(r => r.PurchasedAt)
            .ThenByDescending(r => r.Grant.Id)
            .ToList();

        var items = new List<LibraryItem>(newest.Count);

        foreach (var row in newest)
        {
            var grant = row.Grant;
            var blocker = grant.ServeBlocker(now);
            var token = blocker is null ? delivery.UnprotectToken(grant) : null;

            items.Add(new LibraryItem(
                GrantId: grant.Id,
                BookId: grant.BookId,
                TitleEn: row.Item?.TitleEnSnapshot ?? "",
                TitleAr: row.Item?.TitleArSnapshot ?? "",
                AuthorEn: row.Item?.AuthorEnSnapshot ?? "",
                AuthorAr: row.Item?.AuthorArSnapshot ?? "",
                OrderNumber: row.OrderNumber,
                PurchasedAt: row.PurchasedAt,
                Used: grant.UsedCount,
                Max: grant.MaxDownloads,
                Left: Math.Max(0, grant.MaxDownloads - grant.UsedCount),
                ExpiresAt: grant.ExpiresAt,
                // The state the customer is in, not the stored enum: a grant whose row
                // still says Active an hour after it expired is Expired to its owner.
                State: blocker is null ? nameof(GrantState.Active) : blocker,
                DownloadUrl: string.IsNullOrEmpty(token) ? null : DownloadPathPrefix + Uri.EscapeDataString(token),
                // A revoked grant is a refund or an abuse decision, and re-issuing it
                // would hand back what an operator deliberately took away.
                CanReIssue: blocker is "expired" or "exhausted"));
        }

        return items;
    }

    /// <summary>
    /// AC-4, scoped to the owner. The grant id comes from the customer's own library
    /// listing, but it is still re-checked against their customer id here, because an id
    /// that arrived in a request body is an id anyone can type.
    /// </summary>
    public async Task<(LibraryItem? Item, string? Problem)> ReIssueAsync(
        int customerId, int grantId, CancellationToken ct = default)
    {
        var owned = await db.DownloadGrants
            .AsNoTracking()
            .AnyAsync(g => g.Id == grantId && g.Order!.CustomerId == customerId, ct);

        // Not 403. Confirming that a grant exists but belongs to someone else is a fact
        // about another customer's purchases.
        if (!owned) return (null, "not_found");

        var (issued, problem) = await delivery.ReIssueAsync(grantId, ct);
        if (issued is null) return (null, problem ?? "not_found");

        var library = await LibraryAsync(customerId, ct);
        return (library.FirstOrDefault(i => i.GrantId == issued.Grant.Id), null);
    }

    public Task<List<OrderSummary>> OrdersAsync(int customerId, CancellationToken ct = default) =>
        db.Orders
            .AsNoTracking()
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Select(o => new OrderSummary(
                o.OrderNumber, o.CreatedAt, o.PaidAt, o.Status.ToString(),
                o.Currency, o.TotalKwd, o.Items.Count))
            .ToListAsync(ct);

    public async Task<Invoice?> InvoiceAsync(int customerId, string orderNumber, CancellationToken ct = default)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.OrderNumber == orderNumber && o.CustomerId == customerId, ct);

        if (order is null) return null;

        return new Invoice(
            order.OrderNumber,
            order.CreatedAt,
            order.PaidAt,
            order.Status.ToString(),
            // The snapshots, never the Customer row: that row is mutable and, for a guest
            // checkout, was writable from an unauthenticated request.
            order.BuyerNameSnapshot,
            order.BuyerEmailSnapshot,
            Lang.Normalise(order.Language),
            order.Currency,
            order.SubtotalKwd,
            order.VatRate,
            order.VatAmount,
            order.TotalKwd,
            order.RefundedTotalKwd,
            [.. order.Items
                .OrderBy(i => i.Id)
                .Select(i => new InvoiceLine(
                    i.TitleEnSnapshot, i.TitleArSnapshot, i.AuthorEnSnapshot, i.AuthorArSnapshot, i.UnitPriceKwd))]);
    }
}
