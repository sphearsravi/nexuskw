using Nexus.Application.Common;
using Nexus.Domain.Entities;
using Nexus.Infrastructure.Admin;

namespace Nexus.UnitTests.Admin;

/// <summary>
/// The field rules the form and the bulk import share. Shared on purpose: two copies
/// drift, and the copy that drifts is the one that lets a 2000-character description
/// past the form and into a column that truncates it.
/// </summary>
public sealed class AdminBookValidationTests
{
    private static readonly HashSet<string> Slugs = ["investing", "islamic"];

    private static BookWrite Good() => new(
        "investing", 6.500m, 220, 2025, null,
        "The Patient Portfolio", "Nadia Al-Sabah", "A long-horizon guide.",
        "المحفظة", "نادية", "دليل.",
        "ArabicAndEnglish", null);

    private static List<string> Fields(BookWrite write) =>
        BookValidation.Check(BookValidation.Normalise(write), Slugs).Select(e => e.Field).ToList();

    [Fact]
    public void A_complete_record_passes()
    {
        Assert.Empty(Fields(Good()));
    }

    /// <summary>
    /// Arabic is a first-class edition, so its absence is three errors and not a warning.
    /// </summary>
    [Fact]
    public void The_arabic_edition_is_required()
    {
        var errors = Fields(Good() with { TitleAr = "", AuthorAr = " ", DescriptionAr = "" });

        Assert.Contains("titleAr", errors);
        Assert.Contains("authorAr", errors);
        Assert.Contains("descriptionAr", errors);
    }

    [Fact]
    public void Every_problem_is_reported_at_once_not_the_first_one()
    {
        var errors = Fields(Good() with { TitleAr = "", PriceKwd = 0m, Pages = 0, CategorySlug = "nope" });

        Assert.Equal(4, errors.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_free_or_negative_price_is_refused(decimal price)
    {
        Assert.Contains("priceKwd", Fields(Good() with { PriceKwd = price }));
    }

    /// <summary>
    /// KWD has three decimals. A fourth is not a rounding question — it is a number the
    /// operator did not mean, and storing it silently rounds the price they see.
    /// </summary>
    [Fact]
    public void A_fourth_decimal_is_refused_rather_than_rounded()
    {
        Assert.Contains("priceKwd", Fields(Good() with { PriceKwd = 6.5001m }));
    }

    [Fact]
    public void Three_decimals_are_accepted()
    {
        Assert.Empty(Fields(Good() with { PriceKwd = 6.125m }));
    }

    [Fact]
    public void An_unknown_subject_is_refused()
    {
        Assert.Contains("categorySlug", Fields(Good() with { CategorySlug = "astrology" }));
    }

    [Fact]
    public void A_motif_that_does_not_exist_is_refused()
    {
        Assert.Contains("coverMotif", Fields(Good() with { CoverMotif = "dragons" }));
        Assert.Empty(Fields(Good() with { CoverMotif = "islamic" }));
    }

    [Fact]
    public void A_title_longer_than_the_column_is_refused_rather_than_truncated()
    {
        Assert.Contains("titleEn", Fields(Good() with { TitleEn = new string('x', BookValidation.MaxTitle + 1) }));
    }

    [Fact]
    public void A_year_in_the_far_future_is_refused()
    {
        Assert.Contains("year", Fields(Good() with { Year = DateTimeOffset.UtcNow.Year + 5 }));
    }

    [Fact]
    public void An_unknown_content_language_is_refused_but_an_empty_one_defaults()
    {
        Assert.Contains("contentLanguages", Fields(Good() with { ContentLanguages = "Klingon" }));
        Assert.Empty(Fields(Good() with { ContentLanguages = "" }));
        Assert.Equal(BookLanguages.ArabicAndEnglish, BookValidation.ParseLanguages(""));
        Assert.Equal(BookLanguages.EnglishOnly, BookValidation.ParseLanguages("englishonly"));
    }

    [Fact]
    public void Normalising_trims_and_lowercases_the_keys_but_not_the_prose()
    {
        var write = BookValidation.Normalise(Good() with
        {
            CategorySlug = "  INVESTING ",
            TitleEn = "  Spaced Title  ",
            Isbn = "   "
        });

        Assert.Equal("investing", write.CategorySlug);
        Assert.Equal("Spaced Title", write.TitleEn);
        Assert.Null(write.Isbn);
    }

    /// <summary>
    /// The import's errors carry the line, because "row 23 is wrong" is the only useful
    /// thing to say to someone holding a spreadsheet.
    /// </summary>
    [Fact]
    public void An_import_error_carries_its_line_number()
    {
        var errors = BookValidation.Check(BookValidation.Normalise(Good() with { TitleAr = "" }), Slugs, line: 23);

        Assert.All(errors, e => Assert.Equal(23, e.Line));
    }
}

/// <summary>
/// The settings allow-list. The same table will hold the gateway keys, so what is
/// <i>absent</i> from this list is the part that matters.
/// </summary>
public sealed class AdminSettingsCatalogTests
{
    [Fact]
    public void Only_delivery_and_email_keys_are_editable_from_a_browser()
    {
        Assert.All(AdminSettingsService.Editable, spec =>
            Assert.True(spec.Key.StartsWith("delivery.", StringComparison.Ordinal) ||
                        spec.Key.StartsWith("email.", StringComparison.Ordinal),
                $"{spec.Key} is editable from the panel."));
    }

    [Fact]
    public void No_credential_is_editable_from_the_panel()
    {
        foreach (var word in new[] { "key", "secret", "password", "token", "credential" })
        {
            Assert.DoesNotContain(AdminSettingsService.Editable,
                spec => spec.Key.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_two_publicly_readable_keys_are_both_editable_by_the_operator()
    {
        foreach (var key in SettingKeys.PubliclyReadable)
            Assert.Contains(AdminSettingsService.Editable, spec => spec.Key == key);
    }

    [Fact]
    public void Every_numeric_setting_declares_the_range_it_is_clamped_into()
    {
        Assert.All(AdminSettingsService.Editable.Where(s => s.Kind == SettingKind.Integer), spec =>
        {
            Assert.NotNull(spec.Min);
            Assert.NotNull(spec.Max);
            Assert.True(spec.Min < spec.Max, spec.Key);
        });
    }
}
