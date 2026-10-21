using System.Globalization;
using Nexus.Infrastructure.Email;

namespace Nexus.UnitTests.Email;

public class LangTests
{
    [Theory]
    [InlineData("ar", "ar")]
    [InlineData("AR", "ar")]
    [InlineData("ar-KW", "ar")]
    [InlineData("ar ", "ar")]
    [InlineData(" AR-kw ", "ar")]
    [InlineData("en", "en")]
    [InlineData("en-GB", "en")]
    public void Recognised_tags_normalise_to_two_letters(string raw, string expected)
    {
        Assert.True(Lang.TryNormalise(raw, out var language));
        Assert.Equal(expected, language);
    }

    // Falls back to English AND says so, because answering an Arabic buyer in English is a
    // silent product failure, not a harmless default.
    [Theory]
    [InlineData("fr")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("gibberish")]
    public void Unrecognised_tags_fall_back_to_English_and_report_it(string? raw)
    {
        Assert.False(Lang.TryNormalise(raw, out var language));
        Assert.Equal("en", language);
    }
}

public class MoneyTextTests
{
    [Theory]
    [InlineData(6.5, "KD 6.500")]
    [InlineData(18.389, "KD 18.389")]
    [InlineData(0.5, "KD 0.500")]
    public void English_puts_the_code_first(decimal amount, string expected) =>
        Assert.Equal(expected, MoneyText.Format(amount, "en"));

    [Theory]
    [InlineData(6.5, "6.500 د.ك")]
    [InlineData(18.389, "18.389 د.ك")]
    public void Arabic_puts_the_amount_first(decimal amount, string expected) =>
        Assert.Equal(expected, MoneyText.Format(amount, "ar"));

    // The rule the whole product turns on. "6.50" is not a KWD price.
    [Fact]
    public void Three_decimals_survive_a_two_decimal_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("KD 6.500", MoneyText.Format(6.5m, "en"));
            Assert.Equal("6.500 د.ك", MoneyText.Format(6.5m, "ar"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

public class KuwaitDatesTests
{
    // 22:30 UTC is already the next day in Kuwait. An order placed then must not be dated
    // yesterday on the receipt.
    [Fact]
    public void An_instant_near_midnight_renders_the_Kuwait_calendar_day()
    {
        var instant = new DateTimeOffset(2026, 10, 21, 22, 30, 0, TimeSpan.Zero);

        Assert.Equal("22 October 2026", KuwaitDates.Format(instant, "en"));
        Assert.Equal("22 أكتوبر 2026", KuwaitDates.Format(instant, "ar"));
    }

    [Fact]
    public void Kuwait_is_three_hours_ahead_with_no_daylight_saving()
    {
        var january = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var july = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(3), KuwaitDates.ToKuwait(january).Offset);
        Assert.Equal(TimeSpan.FromHours(3), KuwaitDates.ToKuwait(july).Offset);
    }

    // The Gulf month names, not the Levantine ones a generic Arabic culture may produce,
    // and never Arabic-Indic digits — which is what ICU does for some Arabic cultures.
    [Fact]
    public void Arabic_months_come_from_the_fixed_array_in_Western_digits()
    {
        for (var month = 1; month <= 12; month++)
        {
            var rendered = KuwaitDates.Format(
                new DateTimeOffset(2026, month, 15, 9, 0, 0, TimeSpan.FromHours(3)), "ar");

            Assert.Contains("15", rendered, StringComparison.Ordinal);
            Assert.Contains("2026", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("كانون", rendered, StringComparison.Ordinal);
            Assert.False(ArabicDigits.Any(rendered), rendered);
        }
    }
}

public class QuantityPhraseTests
{
    // English has two cases; Arabic has four, and the defaults (7 and 3) happen to read
    // correctly in the naive form — which is exactly why "{{n}} أيام" would have passed
    // review and broken the day an operator set the expiry to one day.
    [Theory]
    [InlineData(1, "1 day")]
    [InlineData(2, "2 days")]
    [InlineData(7, "7 days")]
    [InlineData(30, "30 days")]
    public void English_days(int n, string expected) =>
        Assert.Equal(expected, QuantityPhrase.Days(n, "en", n.ToString(CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(1, "يوم واحد")]
    [InlineData(2, "يومان")]
    [InlineData(3, "3 أيام")]
    [InlineData(10, "10 أيام")]
    [InlineData(11, "11 يوماً")]
    [InlineData(30, "30 يوماً")]
    public void Arabic_days(int n, string expected) =>
        Assert.Equal(expected, QuantityPhrase.Days(n, "ar", n.ToString(CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(1, "1 download")]
    [InlineData(3, "3 downloads")]
    public void English_downloads(int n, string expected) =>
        Assert.Equal(expected, QuantityPhrase.Downloads(n, "en", n.ToString(CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(1, "عملية تحميل واحدة")]
    [InlineData(2, "عمليتا تحميل")]
    [InlineData(3, "3 عمليات تحميل")]
    [InlineData(10, "10 عمليات تحميل")]
    [InlineData(11, "11 عملية تحميل")]
    public void Arabic_downloads(int n, string expected) =>
        Assert.Equal(expected, QuantityPhrase.Downloads(n, "ar", n.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>PRD §5: Arabic-Indic numerals never appear, in any language, anywhere.</summary>
public static class ArabicDigits
{
    public static bool Any(string value) =>
        value.Any(c => c is >= '٠' and <= '٩' or >= '۰' and <= '۹');

    public static IEnumerable<char> In(string value) =>
        value.Where(c => c is >= '٠' and <= '٩' or >= '۰' and <= '۹');
}
