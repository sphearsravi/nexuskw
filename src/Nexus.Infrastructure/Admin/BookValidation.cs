using System.Globalization;
using Nexus.Domain.Entities;

namespace Nexus.Infrastructure.Admin;

/// <summary>
/// One rejected field. <see cref="Line"/> is 0 for a form and the CSV line number for an
/// import, so the same validator serves both and the operator is told exactly where to look.
/// </summary>
public sealed record FieldError(int Line, string Field, string Code, string? Detail = null);

/// <summary>
/// The field rules for a book, shared by the form and the bulk import.
/// <para>
/// Shared deliberately. Two copies drift, and the copy that drifts is the one that lets a
/// 2000-character description past the form and into a column that truncates it.
/// </para>
/// </summary>
public static class BookValidation
{
    public const int MaxTitle = 256;
    public const int MaxAuthor = 128;
    public const int MaxDescription = 2000;
    public const int MaxIsbn = 32;

    /// <summary>A price nobody typed by accident. Three decimals; KWD has no more.</summary>
    public const decimal MaxPrice = 999.999m;

    public const int MinYear = 1400;

    public static readonly string[] ContentLanguageNames =
        [nameof(BookLanguages.ArabicAndEnglish), nameof(BookLanguages.EnglishOnly), nameof(BookLanguages.ArabicOnly)];

    /// <summary>
    /// Every problem with <paramref name="write"/>, not the first. An import that reports
    /// one error per attempt makes a fifty-row file a fifty-round correspondence.
    /// </summary>
    public static List<FieldError> Check(
        BookWrite write, IReadOnlySet<string> categorySlugs, int line = 0)
    {
        var errors = new List<FieldError>();

        void Text(string? value, string field, int max, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                if (required) errors.Add(new FieldError(line, field, "required"));
                return;
            }

            if (value.Length > max)
                errors.Add(new FieldError(line, field, "too_long", max.ToString(CultureInfo.InvariantCulture)));
        }

        // Both languages, always: a title cannot be published without its Arabic edition,
        // so refusing to store a half-filled record is the kinder failure.
        Text(write.TitleEn, "titleEn", MaxTitle, required: true);
        Text(write.AuthorEn, "authorEn", MaxAuthor, required: true);
        Text(write.DescriptionEn, "descriptionEn", MaxDescription, required: true);
        Text(write.TitleAr, "titleAr", MaxTitle, required: true);
        Text(write.AuthorAr, "authorAr", MaxAuthor, required: true);
        Text(write.DescriptionAr, "descriptionAr", MaxDescription, required: true);
        Text(write.Isbn, "isbn", MaxIsbn, required: false);

        if (string.IsNullOrWhiteSpace(write.CategorySlug))
            errors.Add(new FieldError(line, "categorySlug", "required"));
        else if (!categorySlugs.Contains(write.CategorySlug))
            errors.Add(new FieldError(line, "categorySlug", "unknown_category", write.CategorySlug));

        // No free books (decision D7): a zero-amount charge never reaches the payment path.
        if (write.PriceKwd <= 0m) errors.Add(new FieldError(line, "priceKwd", "must_be_positive"));
        else if (write.PriceKwd > MaxPrice) errors.Add(new FieldError(line, "priceKwd", "too_large"));
        else if (decimal.Round(write.PriceKwd, 3) != write.PriceKwd)
            errors.Add(new FieldError(line, "priceKwd", "too_many_decimals"));

        if (write.Pages <= 0) errors.Add(new FieldError(line, "pages", "must_be_positive"));
        else if (write.Pages > 20_000) errors.Add(new FieldError(line, "pages", "too_large"));

        var thisYear = DateTimeOffset.UtcNow.Year;
        if (write.Year < MinYear || write.Year > thisYear + 1)
            errors.Add(new FieldError(line, "year", "out_of_range"));

        if (!string.IsNullOrWhiteSpace(write.ContentLanguages) &&
            !ContentLanguageNames.Contains(write.ContentLanguages, StringComparer.OrdinalIgnoreCase))
            errors.Add(new FieldError(line, "contentLanguages", "unknown_value", write.ContentLanguages));

        /* The motif is drawn by the storefront from a fixed set keyed on the category
         * slugs, so an unknown value renders an empty cover rather than failing loudly. */
        if (!string.IsNullOrWhiteSpace(write.CoverMotif) && !categorySlugs.Contains(write.CoverMotif))
            errors.Add(new FieldError(line, "coverMotif", "unknown_motif", write.CoverMotif));

        return errors;
    }

    public static BookLanguages ParseLanguages(string? value) =>
        Enum.TryParse<BookLanguages>(value, ignoreCase: true, out var parsed)
            ? parsed
            : BookLanguages.ArabicAndEnglish;

    /// <summary>Trims every field once, so no rule has to wonder whether it was trimmed.</summary>
    public static BookWrite Normalise(BookWrite write) => write with
    {
        CategorySlug = (write.CategorySlug ?? "").Trim().ToLowerInvariant(),
        TitleEn = (write.TitleEn ?? "").Trim(),
        AuthorEn = (write.AuthorEn ?? "").Trim(),
        DescriptionEn = (write.DescriptionEn ?? "").Trim(),
        TitleAr = (write.TitleAr ?? "").Trim(),
        AuthorAr = (write.AuthorAr ?? "").Trim(),
        DescriptionAr = (write.DescriptionAr ?? "").Trim(),
        Isbn = string.IsNullOrWhiteSpace(write.Isbn) ? null : write.Isbn.Trim(),
        ContentLanguages = string.IsNullOrWhiteSpace(write.ContentLanguages) ? null : write.ContentLanguages.Trim(),
        CoverMotif = string.IsNullOrWhiteSpace(write.CoverMotif) ? null : write.CoverMotif.Trim().ToLowerInvariant()
    };
}
