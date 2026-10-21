using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexus.IntegrationTests;

/// <summary>
/// The things that let a shop be found, and the things that must never be found.
/// <para>
/// In the shared collection even though it builds its own hosts: the sitemap reads the
/// catalogue from the same database the collection fixture drops and recreates, and
/// outside the collection those two race — the symptom is a 500 carrying SQL error 207,
/// invalid column name, from a query that met a half-built schema.
/// </para>
/// </summary>
[Collection(NexusCollection.Name)]
public sealed class DiscoveryTests
{
    private sealed class Host(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Nexus"] = NexusApp.LocalDefault,
                    ["Nexus:MigrateOnStartup"] = "false",
                    ["Site:PublicBaseUrl"] = "https://nexus.example",
                }));
        }
    }

    /// <summary>
    /// A staging copy competing with the real store in search results is tedious to
    /// undo once indexed, so anything that is not production refuses everything.
    /// </summary>
    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public async Task A_non_production_deployment_asks_not_to_be_indexed(string environment)
    {
        using var app = new Host(environment);
        var body = await app.CreateClient().GetStringAsync("/robots.txt");

        Assert.Contains("User-agent: *", body, StringComparison.Ordinal);
        Assert.Contains("Disallow: /", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Allow: /", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_opens_the_shop_and_closes_everything_private()
    {
        using var app = new Host("Production");
        var body = await app.CreateClient().GetStringAsync("/robots.txt");

        Assert.Contains("Allow: /", body, StringComparison.Ordinal);
        Assert.Contains("Sitemap: https://nexus.example/sitemap.xml", body, StringComparison.Ordinal);

        // A crawler has no business in any of these, and some of them carry a credential.
        foreach (var path in new[] { "/admin/", "/api/", "/download.html", "/success.html", "/library.html" })
            Assert.Contains($"Disallow: {path}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_sitemap_is_valid_xml_and_lists_the_published_catalogue()
    {
        using var app = new Host("Production");
        var xml = await app.CreateClient().GetStringAsync("/sitemap.xml");

        var doc = XDocument.Parse(xml);   // throws if malformed, which is the assertion
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        var urls = doc.Root!.Elements(ns + "url").Select(u => u.Element(ns + "loc")!.Value).ToList();

        Assert.Contains("https://nexus.example/", urls);
        Assert.Contains("https://nexus.example/shop.html", urls);
        Assert.All(urls, u => Assert.StartsWith("https://nexus.example", u, StringComparison.Ordinal));

        // Nothing private, ever.
        Assert.DoesNotContain(urls, u => u.Contains("/admin", StringComparison.Ordinal)
                                      || u.Contains("download.html", StringComparison.Ordinal)
                                      || u.Contains("success.html", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_favicon_is_served()
    {
        using var app = new Host("Development");
        var response = await app.CreateClient().GetAsync("/favicon.svg");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
