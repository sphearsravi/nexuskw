using Nexus.Infrastructure.Delivery;

namespace Nexus.UnitTests.Delivery;

/// <summary>
/// The library holds the entire product. Nothing a client sends may ever reach a path
/// in it, and "impossible by construction" survives exactly one careless refactor — so
/// the validation is tested, not assumed.
/// </summary>
public sealed class LibraryPathsTests
{
    private static readonly string Sha = new('a', 64);
    private readonly LibraryPaths _paths = new(Path.Combine(Path.GetTempPath(), "nexus-paths-test"));

    [Fact]
    public void A_server_derived_key_is_accepted()
    {
        var key = LibraryPaths.KeyFor(12, Sha);

        Assert.Equal($"12/{Sha}.pdf", key);
        Assert.True(LibraryPaths.IsValidKey(key));
        Assert.NotNull(_paths.ResolveLibrary(key));
    }

    [Theory]
    [InlineData("../dpkeys/key.xml")]
    [InlineData("12/../../dpkeys/key.xml")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\x.pdf")]
    [InlineData("12/%2e%2e/secret.pdf")]
    [InlineData("12/..%2fsecret.pdf")]
    [InlineData("0/" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.pdf")]
    [InlineData("12/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.pdf")]
    [InlineData("12/aaa.pdf")]
    [InlineData("12/" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.exe")]
    [InlineData(null)]
    public void Anything_else_never_reaches_the_filesystem(string? key)
    {
        Assert.False(LibraryPaths.IsValidKey(key));
        Assert.Null(_paths.ResolveLibrary(key));
    }

    /// <summary>
    /// The grant id, not the token: a token in a filename is a bearer credential in
    /// directory listings, backups and every stack trace that prints a path.
    /// </summary>
    [Fact]
    public void The_stamped_cache_key_carries_the_grant_and_not_the_token()
    {
        var key = LibraryPaths.StampedKeyFor(258, Sha);

        Assert.Equal("02/258-" + Sha[..16] + ".pdf", key);
        Assert.NotNull(_paths.ResolveStamped(key));
    }

    /// <summary>
    /// Without the source hash, an operator who uploads a corrected PDF keeps serving
    /// the old one to everybody who had already downloaded once.
    /// </summary>
    [Fact]
    public void A_replaced_source_is_a_different_stamped_key() =>
        Assert.NotEqual(
            LibraryPaths.StampedKeyFor(7, Sha),
            LibraryPaths.StampedKeyFor(7, new string('b', 64)));

    [Fact]
    public void A_stamped_key_that_escapes_the_cache_is_refused() =>
        Assert.Null(_paths.ResolveStamped("../library/12/x.pdf"));

    [Theory]
    [InlineData("The Patient Portfolio", "the-patient-portfolio")]
    [InlineData("  Sukuk & Shari'ah  ", "sukuk-shari-ah")]
    [InlineData("المحفظة الصبورة", "book-9")]          // nothing Latin survives
    [InlineData("Title\r\nInjected: header", "title-injected-header")]
    [InlineData("", "book-9")]
    [InlineData(null, "book-9")]
    public void A_title_becomes_a_filename_that_cannot_carry_a_header(string? title, string expected) =>
        Assert.Equal(expected, LibraryPaths.Slug(title, 9));

    [Fact]
    public void A_very_long_title_is_bounded()
    {
        var slug = LibraryPaths.Slug(new string('t', 300), 1);

        Assert.Equal(60, slug.Length);
    }
}
