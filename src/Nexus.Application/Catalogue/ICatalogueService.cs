namespace Nexus.Application.Catalogue;

public interface ICatalogueService
{
    /// <summary>Active categories in operator order, each with its published count (CAT-06, CAT-07).</summary>
    Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default);

    /// <summary>
    /// Published books matching the query, sorted. Unpublished books are never
    /// returned from here — CAT-02 makes that a property of the read path, not
    /// of each caller remembering to filter.
    /// </summary>
    Task<IReadOnlyList<BookDto>> GetBooksAsync(BookQuery query, CancellationToken ct = default);

    /// <summary>A single published book, or null for unknown, unpublished or malformed (CAT-20).</summary>
    Task<BookDto?> GetBookAsync(int id, CancellationToken ct = default);

    /// <summary>Other published books in the same category, never the book itself (CAT-19).</summary>
    Task<IReadOnlyList<BookDto>> GetRelatedAsync(int bookId, int take = 4, CancellationToken ct = default);
}
