using Nexus.Application.Common;
using Nexus.Infrastructure.Settings;

namespace Nexus.UnitTests.Delivery;

/// <summary>
/// A store that stops selling because somebody typed "seven" into a number field is a
/// worse failure than the typo. Every one of these must degrade, never throw.
/// </summary>
public sealed class SettingsValueTests
{
    [Fact]
    public void A_good_value_is_used_as_written() =>
        Assert.Equal((5, SettingsParse.Ok), SettingsValue.ToInt("5", 3, 1, 20));

    [Theory]
    [InlineData("seven")]
    [InlineData("3.5")]
    [InlineData("٧")]             // Arabic-Indic digits are not a number here
    [InlineData("1e3")]
    [InlineData("--4")]
    public void An_unparseable_value_falls_back(string raw)
    {
        var (value, how) = SettingsValue.ToInt(raw, 3, 1, 20);

        Assert.Equal(3, value);
        Assert.Equal(SettingsParse.Unparseable, how);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("99", 20)]
    [InlineData("2147483647", 20)]
    public void An_out_of_range_value_is_clamped_to_the_bound(string raw, int expected)
    {
        var (value, how) = SettingsValue.ToInt(raw, 3, 1, 20);

        Assert.Equal(expected, value);
        Assert.Equal(SettingsParse.Clamped, how);
    }

    [Fact]
    public void A_missing_row_is_the_fallback_and_not_an_error() =>
        Assert.Equal((3, SettingsParse.Missing), SettingsValue.ToInt(null, 3, 1, 20));

    [Fact]
    public void Whitespace_around_a_number_is_tolerated() =>
        Assert.Equal((9, SettingsParse.Ok), SettingsValue.ToInt("  9 ", 3, 1, 20));

    /// <summary>A fallback outside its own bounds is still clamped. Nothing escapes.</summary>
    [Fact]
    public void Even_a_wrong_fallback_is_clamped() =>
        Assert.Equal(20, SettingsValue.ToInt("nonsense", 500, 1, 20).Value);

    [Fact]
    public void An_integer_overflowing_int_is_clamped_not_wrapped() =>
        Assert.Equal(20, SettingsValue.ToInt("9999999999999", 3, 1, 20).Value);

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("off", false)]
    [InlineData("0", false)]
    public void A_boolean_is_read_generously(string raw, bool expected) =>
        Assert.Equal(expected, SettingsValue.ToBool(raw, false).Value);

    [Fact]
    public void An_unrecognised_boolean_keeps_the_watermark_on() =>
        Assert.True(SettingsValue.ToBool("maybe", true).Value);

    /// <summary>
    /// Pins the public allow-list to the literal list in code. Adding a row to
    /// <c>Settings</c> — the same table that will hold the Tap secret key — can never
    /// widen the endpoint, and this is the test that says so.
    /// </summary>
    [Fact]
    public void The_public_allow_list_is_exactly_two_delivery_keys() =>
        Assert.Equal(
            new[] { "delivery.linkExpiryDays", "delivery.maxDownloads" },
            SettingKeys.PubliclyReadable);
}
