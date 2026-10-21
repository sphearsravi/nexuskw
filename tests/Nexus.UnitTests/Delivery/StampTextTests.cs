using Nexus.Infrastructure.Delivery;

namespace Nexus.UnitTests.Delivery;

/// <summary>
/// PDFsharp draws glyph runs and does no Arabic shaping, so an Arabic name drawn naively
/// comes out as disconnected left-to-right letterforms — worse than absent. The email
/// identifies the buyer either way.
/// </summary>
public sealed class StampTextTests
{
    private static readonly DateTimeOffset Paid = new(2026, 10, 21, 9, 14, 0, TimeSpan.Zero);

    [Fact]
    public void A_latin_name_is_stamped_with_the_email()
    {
        var footer = StampText.Footer("Nadia Al-Sabah", "nadia@example.com", "KW-261021-7F3K2M", Paid);

        Assert.Equal(
            "Licensed to Nadia Al-Sabah · nadia@example.com · Order KW-261021-7F3K2M · 2026-10-21",
            footer);
    }

    [Theory]
    [InlineData("نادية الصباح")]
    [InlineData("Nadia الصباح")]   // half a name is worse than none
    [InlineData("")]
    [InlineData(null)]
    public void A_name_that_cannot_be_drawn_is_omitted_and_the_email_stays(string? name)
    {
        var footer = StampText.Footer(name, "nadia@example.com", "KW-261021-7F3K2M", Paid);

        Assert.StartsWith("Licensed to nadia@example.com ·", footer, StringComparison.Ordinal);
        Assert.Contains("Order KW-261021-7F3K2M", footer, StringComparison.Ordinal);
    }

    [Fact]
    public void The_date_is_western_digits_whatever_the_thread_culture()
    {
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-KW");

        Assert.Contains("2026-10-21", StampText.Footer("A B", "a@b.com", "KW-1", Paid), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Jean-Luc", true)]
    [InlineData("Åsa Lindström", true)]
    [InlineData("محمد", false)]
    [InlineData("   ", false)]
    [InlineData("12345", false)]   // digits alone are not a name
    public void Latin_script_detection_decides(string name, bool expected) =>
        Assert.Equal(expected, StampText.IsLatinScript(name));
}
