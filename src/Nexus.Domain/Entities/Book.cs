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

    // --- the file being sold -------------------------------------------------
    /// <summary>Relative, <c>{bookId}/{sha256}.pdf</c>, under App_Data. Server-derived, never a client filename.</summary>
    public string? PdfStorageKey { get; set; }

    /// <summary>Lowercase hex. Also the filename and the stamp-cache key.</summary>
    public string? PdfSha256 { get; set; }

    public long? PdfSizeBytes { get; set; }
    public int? PdfPageCount { get; set; }

    /// <summary>What the operator uploaded it as. Display only — never used as a path.</summary>
    public string? PdfOriginalFileName { get; set; }

    public DateTimeOffset? PdfUploadedAt { get; set; }
    public string? PdfUploadedByAdminUserId { get; set; }

    /// <summary>
    /// True for the generated stub the seeder writes. Without this flag a seeded
    /// placeholder satisfies "a file exists" and the publish gate reports green on the
    /// exact failure it was added to catch.
    /// </summary>
    public bool PdfIsPlaceholder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Why this book may not be offered for sale, or null when it may. The full gate:
    /// the record is complete <i>and</i> there is a real file to deliver. This is what
    /// the admin's publish action calls.
    /// </summary>
    public string? PublishBlocker()
    {
        var content = ContentBlocker();
        if (content is not null) return content;

        // A title with nothing to deliver must never reach the shop window, and a
        // generated stub must not satisfy "a file exists" — otherwise the gate reports
        // green on the exact failure it was added to catch.
        if (string.IsNullOrWhiteSpace(PdfStorageKey)) return "PDF file is missing.";
        if (PdfIsPlaceholder) return "PDF is a seed placeholder, not the real file.";
        return null;
    }

    /// <summary>
    /// Why this book's <i>record</i> is incomplete, ignoring whether a file exists:
    /// Arabic and English text, a sellable price, an active category.
    /// CAT-03, CAT-04, CAT-34.
    /// <para>
    /// Kept separate from <see cref="PublishBlocker"/> so development and staging can seed
    /// a browsable catalogue without inventing PDFs, while no operator can ever publish a
    /// fileless or placeholder title in any environment.
    /// </para>
    /// </summary>
    public string? ContentBlocker()
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

    /// <summary>Fit to sell: complete record and a real file.</summary>
    public bool CanPublish() => PublishBlocker() is null;

    /// <summary>Complete record, file not considered. What the seeder checks.</summary>
    public bool IsContentComplete() => ContentBlocker() is null;
}

public enum BookLanguages
{
    ArabicAndEnglish = 0,
    EnglishOnly = 1,
    ArabicOnly = 2
}
