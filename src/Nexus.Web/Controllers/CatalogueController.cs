using Microsoft.AspNetCore.Mvc;
using Nexus.Application.Catalogue;

namespace Nexus.Web.Controllers;

/// <summary>
/// The public catalogue. Anonymous by design — this is the shop window.
/// Only published books are reachable; that is enforced in the service, not
/// here, so no future caller can forget it (CAT-02).
/// </summary>
[ApiController]
[Route("api")]
public sealed class CatalogueController(ICatalogueService catalogue) : ControllerBase
{
    [HttpGet("categories")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct) =>
        await catalogue.GetCategoriesAsync(ct);

    /// <summary>
    /// No paging: the catalogue grows ~50 titles a year (D8), so the whole
    /// published set is one small response and the storefront keeps filtering
    /// instantly, with no round-trip per keystroke.
    /// </summary>
    /// <remarks>
    /// Cached by the browser only. No <c>VaryByQueryKeys</c> — that requires the
    /// server-side response-caching middleware, which we deliberately do not
    /// run (see Program.cs), and asking for it without the middleware throws.
    /// </remarks>
    [HttpGet("books")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<IReadOnlyList<BookDto>> Books(
        [FromQuery] string? cat,
        [FromQuery] string? q,
        [FromQuery] string? sort,
        [FromQuery] decimal? minPrice,
        [FromQuery] decimal? maxPrice,
        CancellationToken ct) =>
        await catalogue.GetBooksAsync(new BookQuery(cat, q, sort, minPrice, maxPrice), ct);

    /// <summary>
    /// Not cached. The list may lag an operator's edit by up to the cache
    /// window (CAT-30 allows 60 seconds), but a direct question about one book
    /// must be answered truthfully: a title pulled for a rights problem should
    /// stop resolving at once, not a minute later.
    /// </summary>
    [HttpGet("books/{id:int}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<BookDto>> Book(int id, CancellationToken ct)
    {
        var book = await catalogue.GetBookAsync(id, ct);
        return book is null ? NotFound() : Ok(book);
    }

    [HttpGet("books/{id:int}/related")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Client)]
    public async Task<IReadOnlyList<BookDto>> Related(int id, CancellationToken ct) =>
        await catalogue.GetRelatedAsync(id, 4, ct);
}
