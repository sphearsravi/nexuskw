using Nexus.Domain;
using Nexus.Infrastructure.Orders;

namespace Nexus.UnitTests.Orders;

public sealed class OrdersMoneyTests
{
    [Fact]
    public void Kwd_keeps_three_decimals_through_addition()
    {
        var total = Money.Kwd(6.500m) + Money.Kwd(4.750m) + Money.Kwd(5.000m);

        Assert.Equal(16.250m, total.Amount);
        Assert.Equal("16.250", total.ToString());
    }

    [Fact]
    public void Sum_of_nothing_is_zero_in_the_right_currency()
    {
        var total = Money.Sum([], Currency.Kwd);

        Assert.Equal(0m, total.Amount);
        Assert.Equal("0.000", total.ToString());
    }

    // Rule 1: 6.5 where Tap hashed 6.500 means every signature fails, silently.
    [Theory]
    [InlineData(6.5, "6.500")]
    [InlineData(6, "6.000")]
    [InlineData(0.001, "0.001")]
    [InlineData(1234.5, "1234.500")]
    public void Formatting_is_always_at_the_currency_scale(decimal amount, string expected) =>
        Assert.Equal(expected, Money.Kwd(amount).ToString());

    [Fact]
    public void Rounding_is_away_from_zero_at_three_places()
    {
        Assert.Equal(6.501m, Money.Kwd(6.5005m).Rounded.Amount);
        Assert.Equal(6.500m, Money.Kwd(6.50049m).Rounded.Amount);
    }

    // Mixing currencies is a bug, never a silent conversion: there is no FX step in v1.
    [Fact]
    public void Adding_two_currencies_throws()
    {
        void Mixed() => _ = Money.Kwd(1m) + new Money(1m, Currency.Aed);

        Assert.Throws<InvalidOperationException>(Mixed);
    }

    [Fact]
    public void Sum_rejects_a_line_in_another_currency()
    {
        void Mixed() => Money.Sum([Money.Kwd(1m), new Money(1m, Currency.Aed)], Currency.Kwd);

        Assert.Throws<InvalidOperationException>(Mixed);
    }

    // A thousands separator or an Arabic-Indic digit reaching Tap breaks the hash.
    [Fact]
    public void Formatting_ignores_the_ambient_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-KW");
            Assert.Equal("1234.500", Money.Kwd(1234.5m).ToString());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
