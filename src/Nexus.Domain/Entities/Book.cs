namespace Nexus.Domain.Entities;

/// <summary>
/// A title for sale. One book is one bilingual PDF (decision D5): a single
/// saleable thing with one file, not an Arabic edition and an English one.
/// </summary>
public class Book
{
    public int Id { get; set; }

    public required string CategorySlug { get; set; }
    public Category? Category { get; set; }

    /// <summary>Price in KWD. Three decimals — see <see cref="Currency.Kwd"/>.</summary>
    public decimal PriceKwd { get; set; }

    public int Pages { get; set; }

    /// <summary>Year the book was published by its author, a displayed fact.</summary>
    public int Year { get; set; }

    /// <summary>
    /// When the operator added it to the store. This is what "newest" sorts on
    /// (decision D1), not <see cref="Year"/> — a 2024 title added today should
    /// rank above a 2025 title added last year.
    /// </summary>
    public DateTimeOffset AddedToStoreAt { get; set; }

    /// <summary>
    /// Copies sold. Seeded from the demo figures and maintained from paid
    /// orders in Phase 2 (decision D2), so the storefront does not change when
    /// real sales start.
    /// </summary>
    public int SalesCount { get; set; }

    public string TitleEn { get; set; } = "";
    public string AuthorEn { get; set; } = "";
    public string DescriptionEn { get; set; } = "";

    public string TitleAr { get; set; } = "";
    public string AuthorAr { get; set; } = "";
    public string DescriptionAr { get; set; } = "";

    /// <summary>
    /// Which languages the PDF itself contains, shown on the detail page.
    /// Stored rather than hardcoded (CAT-23) so an English-only title can say so.
    /// </summary>
    public BookLanguages ContentLanguages { get; set; } = BookLanguages.ArabicAndEnglish;

    /// <summary>Overrides the category's cover motif when set (CAT-31).</summary>
    public string? CoverMotif { get; set; }

    public string? Isbn { get; set; }

    public bool IsPublished { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Why this book may not be offered for sale, or null when it may.
    /// The publish gate in one place so the admin, the seeder and any import
    /// cannot drift apart. CAT-03, CAT-04 and CAT-34.
    /// </summary>
    public string? PublishBlocker()
    {
        if (string.IsNullOrWhiteSpace(TitleAr)) return "Arabic title is missing.";
        if (string.IsNullOrWhiteSpace(AuthorAr)) return "Arabic author is missing.";
        if (string.IsNullOrWhiteSpace(DescriptionAr)) return "Arabic description is missing.";
        if (string.IsNullOrWhiteSpace(TitleEn)) return "English title is missing.";
        if (string.IsNullOrWhiteSpace(AuthorEn)) return "English author is missing.";
        if (string.IsNullOrWhiteSpace(DescriptionEn)) return "English description is missing.";
        if (PriceKwd <= 0m) return "Price must be greater than zero.";
        if (Category is { IsActive: false }) return "Category is not active.";
        return null;
    }

    public bool CanPublish() => PublishBlocker() is null;
}

public enum BookLanguages
{
    ArabicAndEnglish = 0,
    EnglishOnly = 1,
    ArabicOnly = 2
}
