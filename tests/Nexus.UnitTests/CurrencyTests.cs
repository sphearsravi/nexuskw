using System.Globalization;
using Nexus.Domain;

namespace Nexus.UnitTests;

public class CurrencyTests
{
    [Fact]
    public void Kwd_is_a_three_decimal_currency() => Assert.Equal(3, Currency.Kwd.Scale);

    [Fact]
    public void Aed_is_a_two_decimal_currency() => Assert.Equal(2, Currency.Aed.Scale);

    // The exact strings Tap receives and hashes. Trailing zeros matter: "6.5" and
    // "6.500" produce different HMACs, and only one of them is accepted.
    [Theory]
    [InlineData("KWD", 6.5, "6.500")]
    [InlineData("KWD", 6.0, "6.000")]
    [InlineData("KWD", 18.75, "18.750")]
    [InlineData("KWD", 0.001, "0.001")]
    [InlineData("AED", 99.4, "99.40")]
    [InlineData("AED", 1234.5, "1234.50")]
    public void Format_pads_to_the_currency_scale(string code, decimal amount, string expected) =>
        Assert.Equal(expected, Currency.FromCode(code).Format(amount));

    [Theory]
    [InlineData("KWD", 6.5005, "6.501")]
    [InlineData("KWD", 6.5004, "6.500")]
    [InlineData("AED", 99.405, "99.41")]
    public void Format_rounds_away_from_zero_at_the_midpoint(
        string code, decimal amount, string expected) =>
        Assert.Equal(expected, Currency.FromCode(code).Format(amount));

    [Fact]
    public void Format_never_emits_a_thousands_separator()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            // de-DE uses '.' for thousands and ',' for decimals — the worst case.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1234.500", Currency.Kwd.Format(1234.5m));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Format_emits_western_digits_under_an_arabic_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-KW");
            Assert.Equal("6.500", Currency.Kwd.Format(6.5m));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("kwd")]
    [InlineData("KWD")]
    public void FromCode_is_case_insensitive(string code) =>
        Assert.Equal(Currency.Kwd, Currency.FromCode(code));

    [Fact]
    public void FromCode_rejects_an_unsupported_currency() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Currency.FromCode("USD"));

    [Fact]
    public void TryFromCode_reports_failure_rather_than_throwing()
    {
        Assert.False(Currency.TryFromCode("USD", out _));
        Assert.False(Currency.TryFromCode(null, out _));
        Assert.True(Currency.TryFromCode("AED", out var aed));
        Assert.Equal(Currency.Aed, aed);
    }
}
