using Nexus.Application.Orders;
using Nexus.Infrastructure.Orders;

namespace Nexus.UnitTests.Orders;

public sealed class OrdersBasketPricingTests
{
    private static PricedLine Line(int id, decimal price) =>
        new(id, price, $"Title {id}", $"عنوان {id}", $"Author {id}", $"مؤلف {id}");

    [Fact]
    public void A_three_book_basket_totals_at_three_decimals()
    {
        var basket = BasketPricing.Quote([Line(3, 6.500m), Line(7, 4.750m), Line(11, 5.000m)]);

        Assert.Equal(16.250m, basket.SubtotalKwd);
        Assert.Equal(16.250m, basket.TotalKwd);
    }

    // Kuwait levies none, and PM-10 forbids the row rendering anywhere.
    [Fact]
    public void There_is_no_vat_and_the_total_is_the_subtotal()
    {
        var basket = BasketPricing.Quote([Line(1, 7.250m)]);

        Assert.Equal(0m, basket.VatRate);
        Assert.Equal(0m, basket.VatAmount);
        Assert.Equal(basket.SubtotalKwd, basket.TotalKwd);
    }

    // Twenty fils-level prices must not drift: rounding happens per line and once on
    // the total, never on every addition.
    [Fact]
    public void Twenty_awkward_prices_do_not_drift()
    {
        var lines = Enumerable.Range(1, 20).Select(i => Line(i, 0.001m * i)).ToList();

        var basket = BasketPricing.Quote(lines);

        Assert.Equal(0.210m, basket.TotalKwd);
        Assert.Equal(210, BasketPricing.ToFils(basket.TotalKwd));
    }

    [Fact]
    public void An_empty_basket_is_refused_rather_than_priced_at_zero()
    {
        var empty = () => BasketPricing.Quote([]);

        Assert.Throws<ArgumentException>(empty);
    }

    // D7: no free books. Unreachable through the publish gate, which is why it is
    // asserted here — a bad import must not become a zero-amount charge.
    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public void A_non_positive_price_is_refused(decimal price)
    {
        var free = () => BasketPricing.Quote([Line(4, 6.500m), Line(9, price)]);

        Assert.Throws<ArgumentException>(free);
    }

    [Fact]
    public void The_lines_come_back_in_the_order_they_went_in()
    {
        var basket = BasketPricing.Quote([Line(11, 1.000m), Line(3, 2.000m)]);

        Assert.Equal([11, 3], basket.Lines.Select(l => l.BookId));
    }

    // C-fils: the comparison with the browser is integer, because comparing a float
    // accumulation to a server decimal produced spurious mismatches.
    [Theory]
    [InlineData(6.5, 6500)]
    [InlineData(16.25, 16250)]
    [InlineData(0.001, 1)]
    [InlineData(18.389, 18389)]
    public void Fils_conversion_is_exact(decimal kwd, int fils) =>
        Assert.Equal(fils, BasketPricing.ToFils(kwd));
}
