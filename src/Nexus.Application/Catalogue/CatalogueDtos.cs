namespace Nexus.Application.Catalogue;

/// <summary>
/// The storefront's existing rendering code reads `b.en.t`, `b.ar.a`, `b.cat`,
/// `b.price` and so on straight off the objects that used to live in data.js.
/// These DTOs serialise to exactly that shape so the markup and the drawing
/// code do not change — the whole point of CAT-01.
/// </summary>
public sealed record LocalisedText(string T, string A, string D);

public sealed record BookDto(
    int Id,
    string Cat,
    decimal Price,
    int Pages,
    int Year,
    int Sales,
    LocalisedText En,
    LocalisedText Ar,
    string? Motif,
    string? Isbn,
    string Languages);

public sealed record CategoryDto(
    string Id,
    string En,
    string Ar,
    string Hue,
    int Count);

/// <summary>What the shop page asks for. All optional; all combine (CAT-11).</summary>
public sealed record BookQuery(
    string? Cat = null,
    string? Q = null,
    string? Sort = null,
    decimal? MinPrice = null,
    decimal? MaxPrice = null);

public static class BookSort
{
    public const string Popular = "popular";
    public const string Newest = "newest";
    public const string PriceAsc = "price-asc";
    public const string PriceDesc = "price-desc";
}
