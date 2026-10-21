using Nexus.Domain.Entities;

namespace Nexus.UnitTests;

/// <summary>
/// The publish gate is the only thing standing between an incomplete or
/// unlicensed record and a live shop window, so it is tested as a unit rather
/// than trusted to the admin form. CAT-03, CAT-04, CAT-34.
/// </summary>
public class PublishGateTests
{
    private static Book Valid() => new()
    {
        Id = 1,
        CategorySlug = "investing",
        Category = new Category { Slug = "investing", NameEn = "Investing", NameAr = "الاستثمار", Hue = "#0B5D4A" },
        PriceKwd = 6.500m,
        TitleEn = "The Patient Portfolio",
        AuthorEn = "Nadia Al-Rashed",
        DescriptionEn = "A case against trading your savings.",
        TitleAr = "المحفظة الصبورة",
        AuthorAr = "نادية الراشد",
        DescriptionAr = "حجة ضد المتاجرة بمدخراتك."
    };

    /// <summary>A record complete enough for development and staging to seed and browse.</summary>
    private static Book WithRealPdf()
    {
        var b = Valid();
        b.PdfStorageKey = "1/abc.pdf";
        b.PdfSha256 = new string('a', 64);
        b.PdfIsPlaceholder = false;
        return b;
    }

    [Fact]
    public void A_complete_record_is_content_complete() =>
        Assert.True(Valid().IsContentComplete());

    /// <summary>
    /// …but not sellable. Completeness and sellability are deliberately separate: a
    /// seeded catalogue needs no PDFs, while nothing fileless may ever be put on sale.
    /// </summary>
    [Fact]
    public void A_complete_record_with_no_file_cannot_be_published()
    {
        var b = Valid();
        Assert.True(b.IsContentComplete());
        Assert.False(b.CanPublish());
        Assert.Equal("PDF file is missing.", b.PublishBlocker());
    }

    [Fact]
    public void A_complete_record_with_a_real_file_can_be_published()
    {
        var b = WithRealPdf();
        Assert.True(b.CanPublish());
        Assert.Null(b.PublishBlocker());
    }

    /// <summary>
    /// The rule blocker B12 exists for: a generated stub satisfies "a file exists", so
    /// without this the gate reports green on the one failure it was added to catch.
    /// </summary>
    [Fact]
    public void A_seed_placeholder_file_cannot_be_published()
    {
        var b = WithRealPdf();
        b.PdfIsPlaceholder = true;
        Assert.Equal("PDF is a seed placeholder, not the real file.", b.PublishBlocker());
    }

    [Fact]
    public void Content_rules_are_checked_before_file_rules()
    {
        var b = WithRealPdf();
        b.TitleAr = "";
        // The missing Arabic is the more useful thing to report first.
        Assert.Equal("Arabic title is missing.", b.PublishBlocker());
    }

    // Arabic is a first-class edition: a book missing any of the three is not
    // publishable, however complete its English is.
    [Fact]
    public void Missing_arabic_title_blocks_publication()
    {
        var b = Valid(); b.TitleAr = "";
        Assert.Equal("Arabic title is missing.", b.PublishBlocker());
    }

    [Fact]
    public void Missing_arabic_author_blocks_publication()
    {
        var b = Valid(); b.AuthorAr = "   ";
        Assert.Equal("Arabic author is missing.", b.PublishBlocker());
    }

    [Fact]
    public void Missing_arabic_description_blocks_publication()
    {
        var b = Valid(); b.DescriptionAr = "";
        Assert.Equal("Arabic description is missing.", b.PublishBlocker());
    }

    [Fact]
    public void Missing_english_text_blocks_publication()
    {
        var b = Valid(); b.TitleEn = "";
        Assert.Equal("English title is missing.", b.PublishBlocker());
    }

    // D7: no free books. A zero-amount charge is a gateway special case we
    // never want to meet in Phase 3.
    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public void A_zero_or_negative_price_blocks_publication(decimal price)
    {
        var b = Valid(); b.PriceKwd = price;
        Assert.Equal("Price must be greater than zero.", b.PublishBlocker());
    }

    [Fact]
    public void The_smallest_sellable_price_is_one_fils()
    {
        var b = WithRealPdf(); b.PriceKwd = 0.001m;
        Assert.True(b.CanPublish());
    }

    [Fact]
    public void An_inactive_category_blocks_publication()
    {
        var b = Valid(); b.Category!.IsActive = false;
        Assert.Equal("Category is not active.", b.PublishBlocker());
    }
}
