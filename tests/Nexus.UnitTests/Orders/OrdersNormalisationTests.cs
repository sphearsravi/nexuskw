using Nexus.Infrastructure.Orders;

namespace Nexus.UnitTests.Orders;

public sealed class OrdersNormalisationTests
{
    // C-lang: "AR", "ar-KW" and "ar " all resolve to ar; anything else is en, and the
    // caller logs it rather than failing a purchase over our own storefront's bug.
    [Theory]
    [InlineData("ar", "ar", true)]
    [InlineData("AR", "ar", true)]
    [InlineData("ar-KW", "ar", true)]
    [InlineData("ar ", "ar", true)]
    [InlineData("en", "en", true)]
    [InlineData("en-GB", "en", true)]
    [InlineData("fr", "en", false)]
    [InlineData("", "en", false)]
    [InlineData(null, "en", false)]
    public void Language_folds_to_two_letters(string? input, string expected, bool recognised)
    {
        Assert.Equal(expected, CheckoutNormalisation.Language(input, out var wasRecognised));
        Assert.Equal(recognised, wasRecognised);
    }

    [Theory]
    [InlineData("fatima.a@example.com")]
    [InlineData("  fatima.a@example.com  ")]
    [InlineData("a+tag@sub.example.co.uk")]
    public void A_usable_address_is_accepted(string email) =>
        Assert.True(CheckoutNormalisation.IsPlausibleEmail(email, 256));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fatima.a")]
    [InlineData("fatima.a@")]
    [InlineData("@example.com")]
    [InlineData("fatima@localhost")]
    [InlineData("fatima@example.")]
    public void An_unusable_address_is_refused(string? email) =>
        Assert.False(CheckoutNormalisation.IsPlausibleEmail(email, 256));

    [Fact]
    public void An_over_long_address_is_refused() =>
        Assert.False(CheckoutNormalisation.IsPlausibleEmail(
            new string('a', 250) + "@example.com", 256));
}
