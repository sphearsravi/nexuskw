using Nexus.Infrastructure.Delivery;

namespace Nexus.UnitTests.Delivery;

/// <summary>
/// The repository is public, so the token format is known and entropy is the only thing
/// protecting a link. These are the properties that entropy depends on.
/// </summary>
public sealed class DownloadTokenTests
{
    [Fact]
    public void A_token_is_forty_three_base64url_characters()
    {
        var token = DownloadToken.New();

        Assert.Equal(43, token.Length);
        Assert.All(token, c => Assert.True(
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"'{c}' is not Base64Url."));
    }

    [Fact]
    public void Ten_thousand_tokens_are_all_different()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 10_000; i++) Assert.True(seen.Add(DownloadToken.New()));
    }

    [Fact]
    public void The_hash_is_lowercase_hex_and_stable()
    {
        var token = DownloadToken.New();
        var hash = DownloadToken.Hash(token);

        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'));
        Assert.Equal(hash, DownloadToken.Hash(token));
        Assert.NotEqual(hash, DownloadToken.Hash(DownloadToken.New()));
    }

    // A shape check before the database means an enumeration attempt costs a string
    // comparison, not an index seek.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("../../../etc/passwd")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]    // 42
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]  // 44
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+/")]   // Base64, not Base64Url
    public void A_token_that_is_not_the_right_shape_is_rejected_before_any_lookup(string? candidate) =>
        Assert.False(DownloadToken.LooksWellFormed(candidate));

    [Fact]
    public void A_freshly_minted_token_passes_the_shape_check() =>
        Assert.True(DownloadToken.LooksWellFormed(DownloadToken.New()));
}
