using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexus.Application.Orders;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Orders;

/// <summary>
/// Prices a basket from the catalogue. The browser sends book ids and nothing with a
/// decimal point in it, so this is the only place in the system where a total comes
/// into existence (PM-3) — there is nothing to compare against and nothing to override.
/// </summary>
public sealed class PricingService(
    NexusDbContext db,
    OrderOptions options,
    ILogger<PricingService> log) : IPricingService
{
    public async Task<(PricedBasket? Basket, BasketProblem? Problem)> PriceAsync(
        IReadOnlyList<int> bookIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bookIds);

        if (bookIds.Count == 0)
        {
            return (null, new BasketProblem("cart_empty", "The cart is empty."));
        }

        if (bookIds.Any(id => id <= 0))
        {
            return (null, new BasketProblem("invalid_items", "Book ids must be positive.",
                bookIds.First(id => id <= 0)));
        }

        // Duplicates are collapsed, not rejected. A book is a file, not a quantity
        // (ST-5), so the client should never send one — and punishing a buyer for a
        // client-side bug when the correct order is unambiguous is the wrong trade.
        var ids = bookIds.Distinct().Order().ToList();

        if (ids.Count > options.MaxCartItems)
        {
            return (null, new BasketProblem("cart_too_large",
                $"A cart holds up to {options.MaxCartItems} books; {ids.Count} were requested."));
        }

        // IsPublished is in the query, not applied afterwards, so an unpublished title
        // cannot be priced even by mistake.
        var rows = await db.Books
            .AsNoTracking()
            .Where(b => ids.Contains(b.Id) && b.IsPublished)
            .Select(b => new
            {
                b.Id,
                b.PriceKwd,
                b.TitleEn,
                b.TitleAr,
                b.AuthorEn,
                b.AuthorAr
            })
            .ToListAsync(ct);

        var found = rows.ToDictionary(r => r.Id);

        // Unknown, unpublished and withdrawn are one answer. CAT-20 established that the
        // catalogue does not confirm the existence of unpublished ids, and the order path
        // must not become the oracle the catalogue refuses to be.
        var missing = ids.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            return (null, new BasketProblem(
                "items_unavailable",
                $"Not available: {string.Join(", ", missing)}.",
                missing[0]));
        }

        var zeroPriced = rows.FirstOrDefault(r => r.PriceKwd <= 0m);
        if (zeroPriced is not null)
        {
            // Unreachable through the publish gate (D7), which is exactly why it is
            // asserted here: a bad import must not become a zero-amount charge.
            log.LogError("Book {BookId} is published at a non-positive price {Price}.",
                zeroPriced.Id, zeroPriced.PriceKwd);

            return (null, new BasketProblem("invalid_price",
                $"Book {zeroPriced.Id} is published at a non-positive price.", zeroPriced.Id));
        }

        var lines = ids
            .Select(id => found[id])
            .Select(r => new PricedLine(r.Id, r.PriceKwd, r.TitleEn, r.TitleAr, r.AuthorEn, r.AuthorAr))
            .ToList();

        return (BasketPricing.Quote(lines), null);
    }
}
