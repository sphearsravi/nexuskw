using Microsoft.EntityFrameworkCore;
using Nexus.Application.Catalogue;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Persistence;

namespace Nexus.Infrastructure.Catalogue;

/// <summary>
/// Reads the published catalogue.
/// <para>
/// The catalogue is small by decision — about fifty titles a year, so ~150
/// after three years (D8) — so filtering and sorting happen over the published
/// set in memory rather than being pushed into SQL with a paging cursor.
/// CAT-37 records the threshold at which that is worth revisiting: 300 books,
/// or a payload over 150 KB compressed.
/// </para>
/// Doing it here also lets Arabic folding (CAT-10) be a plain, tested C#
/// function instead of a collation we would have to trust.
/// </summary>
public sealed class CatalogueService(NexusDbContext db) : ICatalogueService
{
    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var counts = await db.Books
            .Where(b => b.IsPublished)
            .GroupBy(b => b.CategorySlug)
            .Select(g => new { Slug = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Slug, x => x.Count, ct);

        var categories = await db.Categories
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder)
            .AsNoTracking()
            .ToListAsync(ct);

        return categories
            .Select(c => new CategoryDto(c.Slug, c.NameEn, c.NameAr, c.Hue,
                                         counts.TryGetValue(c.Slug, out var n) ? n : 0))
            .ToList();
    }

    public async Task<IReadOnlyList<BookDto>> GetBooksAsync(BookQuery query, CancellationToken ct = default)
    {
        var books = await PublishedAsync(ct);

        if (!string.IsNullOrWhiteSpace(query.Cat))
            books = books.Where(b => b.CategorySlug == query.Cat).ToList();

        if (query.MinPrice is { } min) books = books.Where(b => b.PriceKwd >= min).ToList();
        if (query.MaxPrice is { } max) books = books.Where(b => b.PriceKwd <= max).ToList();

        // CAT-09: matches title and author in BOTH languages, whatever the
        // interface language is. The demo searched only the active one, so an
        // Arabic reader could not find a book by its English title.
        if (!string.IsNullOrWhiteSpace(query.Q))
        {
            var needle = ArabicSearch.Fold(query.Q);
            if (needle.Length > 0)
            {
                books = books.Where(b =>
                    ArabicSearch.Contains(b.TitleEn, needle) ||
                    ArabicSearch.Contains(b.AuthorEn, needle) ||
                    ArabicSearch.Contains(b.TitleAr, needle) ||
                    ArabicSearch.Contains(b.AuthorAr, needle)).ToList();
            }
        }

        return Sort(books, query.Sort).Select(Map).ToList();
    }

    public async Task<BookDto?> GetBookAsync(int id, CancellationToken ct = default)
    {
        var book = await db.Books
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == id && b.IsPublished, ct);

        // CAT-20: unknown, unpublished and malformed all return nothing. The
        // demo fell back to BOOKS[0], so ?id=9999 silently showed a different
        // book — a shareable URL that lies about what it points to.
        return book is null ? null : Map(book);
    }

    public async Task<IReadOnlyList<BookDto>> GetRelatedAsync(int bookId, int take = 4, CancellationToken ct = default)
    {
        var book = await db.Books.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bookId && b.IsPublished, ct);
        if (book is null) return [];

        var related = await db.Books.AsNoTracking()
            .Where(b => b.IsPublished && b.CategorySlug == book.CategorySlug && b.Id != bookId)
            .OrderByDescending(b => b.SalesCount)
            .ThenBy(b => b.Id)
            .Take(take)
            .ToListAsync(ct);

        return related.Select(Map).ToList();
    }

    private async Task<List<Book>> PublishedAsync(CancellationToken ct) =>
        await db.Books.AsNoTracking().Where(b => b.IsPublished).ToListAsync(ct);

    /// <summary>
    /// Every ordering is total (CAT-12): the Id tiebreak makes two identical
    /// requests return the same sequence, rather than whatever order the
    /// database happened to hand back.
    /// </summary>
    private static IEnumerable<Book> Sort(List<Book> books, string? sort) => sort switch
    {
        BookSort.Newest => books.OrderByDescending(b => b.AddedToStoreAt).ThenBy(b => b.Id),
        BookSort.PriceAsc => books.OrderBy(b => b.PriceKwd).ThenBy(b => b.Id),
        BookSort.PriceDesc => books.OrderByDescending(b => b.PriceKwd).ThenBy(b => b.Id),
        _ => books.OrderByDescending(b => b.SalesCount).ThenBy(b => b.Id)
    };

    private static BookDto Map(Book b) => new(
        b.Id,
        b.CategorySlug,
        b.PriceKwd,
        b.Pages,
        b.Year,
        b.SalesCount,
        new LocalisedText(b.TitleEn, b.AuthorEn, b.DescriptionEn),
        new LocalisedText(b.TitleAr, b.AuthorAr, b.DescriptionAr),
        b.CoverMotif,
        b.Isbn,
        b.ContentLanguages switch
        {
            BookLanguages.EnglishOnly => "en",
            BookLanguages.ArabicOnly => "ar",
            _ => "both"
        });
}
