using System.Net;

namespace Nexus.IntegrationTests;

/// <summary>
/// The account pages are served, and each one loads the three scripts it needs.
/// <para>
/// A page that 404s after a rename is the regression this catches; a page that renders
/// a raw i18n key because it forgot <c>i18n.js</c> is the other. The storefront has no
/// build step, so nothing else would notice either.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class AccountsPageTests(NexusApp app)
{
    private readonly HttpClient _client = app.CreateClient();

    public static TheoryData<string> Pages =>
        ["/account.html", "/confirm.html", "/reset.html", "/library.html", "/orders.html",
         "/invoice.html", "/profile.html"];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task The_page_is_served_with_its_scripts(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        foreach (var script in new[] { "assets/js/api.js", "assets/js/i18n.js", "assets/js/site.js" })
            Assert.Contains(script, html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The library no longer reads <c>localStorage</c>. It used to, which made the shelf
    /// empty on a second device and wrong about the download counter everywhere.
    /// </summary>
    [Fact]
    public async Task The_library_page_reads_the_server_and_not_the_browser()
    {
        var html = await _client.GetStringAsync("/library.html");

        Assert.Contains("Account.library()", html, StringComparison.Ordinal);
        Assert.DoesNotContain("state.lib", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token in a query string reaches every access log between here and the reader.
    /// The confirmation and reset pages take theirs from the fragment, which a browser
    /// never sends to a server at all.
    /// </summary>
    [Theory]
    [InlineData("/confirm.html")]
    [InlineData("/reset.html")]
    public async Task The_landing_pages_read_the_token_from_the_fragment(string path)
    {
        var html = await _client.GetStringAsync(path);

        Assert.Contains("location.hash", html, StringComparison.Ordinal);
        Assert.DoesNotContain("location.search", html, StringComparison.Ordinal);
    }
}
