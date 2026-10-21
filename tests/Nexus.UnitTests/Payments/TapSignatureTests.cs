using Microsoft.Extensions.Options;
using Nexus.Application.Payments;
using Nexus.Domain;
using Nexus.Infrastructure.Payments;

namespace Nexus.UnitTests.Payments;

public class TapSignatureTests
{
    private static ITapSignature Sig(string key = "sk_test_unit") =>
        new TapSignature(Options.Create(new TapOptions { SecretKey = key }));

    // --- the shape of the string -------------------------------------------
    // Field order and the literal x_ prefixes are copied from Tap's documentation.
    // A reordering here is undetectable in production except as 100% rejection.

    [Fact]
    public void The_concatenation_matches_the_documented_order()
    {
        var s = Sig().BuildToBeHashed(
            "chg_1", 1.000m, Currency.Kwd, "gw_1", "pay_1", "CAPTURED", "1698392202943");

        Assert.Equal(
            "x_idchg_1x_amount1.000x_currencyKWDx_gateway_referencegw_1" +
            "x_payment_referencepay_1x_statusCAPTUREDx_created1698392202943",
            s);
    }

    /// <summary>
    /// The highest-consequence line in the system. KWD is a three-decimal currency, so
    /// 6.5 must be hashed as "6.500". Hashing "6.50" fails every signature, silently.
    /// </summary>
    [Fact]
    public void A_kwd_amount_is_hashed_at_three_decimals()
    {
        var s = Sig().BuildToBeHashed("chg_1", 6.5m, Currency.Kwd, "", "", "CAPTURED", "1");
        Assert.Contains("x_amount6.500", s, StringComparison.Ordinal);
        Assert.DoesNotContain("x_amount6.50x", s, StringComparison.Ordinal);
    }

    [Fact]
    public void An_aed_amount_is_hashed_at_two_decimals()
    {
        var s = Sig().BuildToBeHashed("chg_1", 99.4m, Currency.Aed, "", "", "CAPTURED", "1");
        Assert.Contains("x_amount99.40", s, StringComparison.Ordinal);
    }

    /// <summary>Tap's sample passes an empty string for a missing gateway reference, not a gap.</summary>
    [Fact]
    public void A_missing_gateway_reference_becomes_an_empty_segment()
    {
        var s = Sig().BuildToBeHashed("chg_1", 1.000m, Currency.Kwd, "", "pay_1", "CAPTURED", "1");
        Assert.Contains("x_gateway_referencex_payment_referencepay_1", s, StringComparison.Ordinal);
    }

    // --- verification -------------------------------------------------------

    [Fact]
    public void A_correct_hash_verifies()
    {
        const string key = "sk_test_abc";
        var toBeHashed = Sig(key).BuildToBeHashed("chg_1", 6.500m, Currency.Kwd, "gw", "pay", "CAPTURED", "1");
        Assert.True(Sig(key).Verify(toBeHashed, TapSignature.Compute(toBeHashed, key)));
    }

    [Fact]
    public void An_uppercase_hash_still_verifies()
    {
        const string key = "sk_test_abc";
        var toBeHashed = Sig(key).BuildToBeHashed("chg_1", 6.500m, Currency.Kwd, "gw", "pay", "CAPTURED", "1");
        var upper = TapSignature.Compute(toBeHashed, key).ToUpperInvariant();
        Assert.True(Sig(key).Verify(toBeHashed, upper));
    }

    [Fact]
    public void A_hash_from_a_different_key_is_rejected()
    {
        var toBeHashed = Sig().BuildToBeHashed("chg_1", 6.500m, Currency.Kwd, "gw", "pay", "CAPTURED", "1");
        Assert.False(Sig("sk_test_right").Verify(toBeHashed, TapSignature.Compute(toBeHashed, "sk_test_wrong")));
    }

    /// <summary>A tampered amount must not verify — this is the whole point of the check.</summary>
    [Fact]
    public void A_tampered_amount_is_rejected()
    {
        const string key = "sk_test_abc";
        var honest = Sig(key).BuildToBeHashed("chg_1", 6.500m, Currency.Kwd, "gw", "pay", "CAPTURED", "1");
        var tampered = Sig(key).BuildToBeHashed("chg_1", 0.001m, Currency.Kwd, "gw", "pay", "CAPTURED", "1");
        Assert.False(Sig(key).Verify(tampered, TapSignature.Compute(honest, key)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    public void A_missing_or_malformed_hash_is_rejected(string provided)
    {
        var toBeHashed = Sig().BuildToBeHashed("chg_1", 6.500m, Currency.Kwd, "gw", "pay", "CAPTURED", "1");
        Assert.False(Sig().Verify(toBeHashed, provided));
    }

    /// <summary>
    /// An unconfigured key must fail loudly. Verifying against an empty secret would make
    /// every forged webhook that happened to hash correctly against "" look genuine.
    /// </summary>
    [Fact]
    public void An_unconfigured_secret_key_throws_rather_than_accepting()
    {
        var sig = new TapSignature(Options.Create(new TapOptions { SecretKey = "" }));
        Assert.Throws<InvalidOperationException>(() => sig.Verify("anything", "deadbeef"));
    }
}
